#!/usr/bin/env bash
# Copyright (c) 2026 LMS Developer. All rights reserved.
# Applicable licence and release scope: see LICENSE.md for details.
set -euo pipefail
[[ $EUID == 0 ]] || { echo 'SFTP policy setup requires root.' >&2; exit 1; }
source_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
safe_directory() {
    python3 - "$1" <<'CHECK'
import os,stat,sys
s=os.lstat(sys.argv[1]); sys.exit(0 if stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and not s.st_mode & 0o022 else 1)
CHECK
}
root_owned() {
    python3 - "$1" <<'CHECK'
import os,sys
sys.exit(0 if os.lstat(sys.argv[1]).st_uid == 0 else 1)
CHECK
}
guard=/usr/local/libexec/lms-sftp-guard
if [[ ${1:-} == check ]]; then
    "$source_dir/lms-sftp-guard" --probe
    [[ -x /usr/lib/openssh/sftp-server ]] || { echo 'Install openssh-sftp-server first.' >&2; exit 1; }
    install -D -o root -g root -m 755 "$source_dir/lms-sftp-guard" "$guard"
    exit
fi
user=${2:-}; root=${3:-}; mask=${4:-}
[[ $user =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || exit 1
config="/etc/ssh/sshd_config.d/00-lms-sftp-policy-$user.conf"
[[ -d /etc/ssh/sshd_config.d ]] || { echo 'OpenSSH drop-in configuration is required.' >&2; exit 1; }
if [[ $1 == remove ]]; then
    if [[ -f $config ]]; then rm -- "$config"; /usr/sbin/sshd -t; systemctl reload ssh; fi
    exit
fi
[[ $1 == apply && $mask =~ ^[0-9]+$ && $mask -le 127 ]] || exit 1
[[ $root == /* && $root != / && $(realpath -- "$root") == "$root" && -O $root ]] || exit 1
# Never follow user-controlled symlinks while installing privileged runtime files.
for path in usr usr/local usr/local/libexec lib lib64 etc dev; do
    current="$root"
    IFS=/ read -ra parts <<< "$path"
    for part in "${parts[@]}"; do
        current="$current/$part"
        [[ ! -L $current ]] || { echo 'Unsafe runtime symlink.' >&2; exit 1; }
        if [[ -e $current ]]; then
            safe_directory "$current" || { echo 'Unsafe runtime directory.' >&2; exit 1; }
        else install -d -o root -g root -m 755 "$current"; fi
    done
done
python3 - "$root/dev/null" <<'NULL'
import os,stat,sys
p=sys.argv[1]
if not os.path.lexists(p): os.mknod(p,stat.S_IFCHR|0o666,os.makedev(1,3))
s=os.lstat(p)
if not stat.S_ISCHR(s.st_mode) or s.st_rdev != os.makedev(1,3) or s.st_uid != 0:
    raise SystemExit('Unsafe /dev/null in SFTP chroot')
os.chmod(p,0o666)
NULL
copy_runtime() {
    local from=$1 to="$root$2"
    local parent="$root" relative=${2%/*} part mode
    IFS=/ read -ra parts <<< "$relative"
    for part in "${parts[@]}"; do
        [[ -n $part ]] || continue
        parent="$parent/$part"
        [[ ! -L $parent ]] || exit 1
        if [[ -e $parent ]]; then
            safe_directory "$parent" || exit 1
        else install -d -o root -g root -m 755 "$parent"; fi
    done
    [[ ! -L $to ]] || exit 1
    if [[ -e $to ]]; then root_owned "$to" || exit 1; fi
    install -D -o root -g root -m 755 -- "$from" "$to"
}
copy_runtime "$guard" "$guard"
copy_runtime /usr/lib/openssh/sftp-server /usr/local/libexec/lms-sftp-server
while read -r dependency; do
    [[ $dependency == /* && -f $dependency ]] || continue
    copy_runtime "$dependency" "$dependency"
done < <(ldd /usr/lib/openssh/sftp-server | awk '{for(i=1;i<=NF;i++) if($i ~ /^\//) print $i}')
# Minimal identity data only; never copy host password databases into a chroot.
[[ ! -L $root/etc/passwd && ! -L $root/etc/group && ! -L $root/.lms-sftp-policy ]] || exit 1
getent passwd "$user" > "$root/etc/passwd"
getent group "$(id -g "$user")" > "$root/etc/group"
backup=$(mktemp); existed=false
policy_backup=$(mktemp); policy_existed=false
if [[ -f $config ]]; then cp -- "$config" "$backup"; existed=true; fi
if [[ -f $root/.lms-sftp-policy ]]; then cp -- "$root/.lms-sftp-policy" "$policy_backup"; policy_existed=true; fi
rollback() {
    if $existed; then cp -- "$backup" "$config"; else rm -f -- "$config"; fi
    if $policy_existed; then cp -- "$policy_backup" "$root/.lms-sftp-policy"; else rm -f -- "$root/.lms-sftp-policy"; fi
    rm -f -- "$backup" "$policy_backup"
}
trap rollback ERR
policy_stage=$(mktemp "$root/.lms-sftp-policy.XXXXXX")
printf '%s\n' "$mask" > "$policy_stage"
chown root:root "$root/etc/passwd" "$root/etc/group" "$policy_stage"
chmod 644 "$root/etc/passwd" "$root/etc/group" "$policy_stage"
mv -f -- "$policy_stage" "$root/.lms-sftp-policy"
printf 'Match User %s\n    ForceCommand %s\nMatch all\n' "$user" "$guard" > "$config"
chmod 644 "$config"; chown root:root "$config"
/usr/sbin/sshd -t
resolved=$(/usr/sbin/sshd -T -C "user=$user,host=localhost,addr=127.0.0.1")
grep -Fx "forcecommand $guard" <<< "$resolved" >/dev/null
actual_root=$(awk '$1 == "chrootdirectory" { print $2 }' <<< "$resolved")
[[ ${actual_root//%u/$user} == "$root" ]]
systemctl reload ssh
trap - ERR
rm -f -- "$backup" "$policy_backup"
echo 'SFTP filesystem permissions applied. Reconnect existing SFTP sessions.'
