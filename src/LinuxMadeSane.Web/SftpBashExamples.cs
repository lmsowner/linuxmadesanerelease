// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE.md for details.
namespace LinuxMadeSane.Web;

internal static class SftpBashExamples
{
    internal const string Connect = """
        set -euo pipefail
        umask 077
        read -rsp 'LMS application token: ' token; echo
        work=$(mktemp -d)
        trap 'rm -rf "$work"' EXIT
        printf 'Authorization: Bearer %s\n' "$token" > "$work/header"
        unset token
        api() {
            curl --silent --show-error --fail --max-time 60 -H "@$work/header" "$@"
        }
        api "https://sftp.example.net:5443/api/sftpserver/" | jq .
        api "https://sftp.example.net:5443/api/sftpserver/server/status" |
            jq -e '.managed and .sshActive and .configurationValid' >/dev/null
        """;

    internal const string Provision = """
        owned() {
            api "https://sftp.example.net:5443/api/sftpserver/users/import_user/exists" |
                jq -r '.exists | if type == "boolean" then . else error("Invalid response") end'
        }
        exists=$(owned)
        if [[ $exists == false ]]; then
            mode=PublicKeyOnly  # PasswordOnly, PublicKeyOnly or PasswordAndPublicKey
            jq -n --arg mode "$mode" '{username:"import_user", authenticationMode:$mode}' > "$work/body"
            if [[ $mode != PublicKeyOnly ]]; then
                read -rsp 'New customer password: ' password; echo
                printf %s "$password" | jq -Rs --slurpfile b "$work/body" '$b[0] + {password:.}' > "$work/new"
                unset password
                mv "$work/new" "$work/body"
            fi
            if [[ $mode != PasswordOnly ]]; then
                jq --rawfile key customer.pub '. + {publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' "$work/body" > "$work/new"
                mv "$work/new" "$work/body"
            fi
            rc=0
            status=$(curl --silent --show-error --max-time 60 -H "@$work/header" --json "@$work/body" \
                -o "$work/result" -w '%{http_code}' "https://sftp.example.net:5443/api/sftpserver/users") || rc=$?
            rm "$work/body"
            if [[ $rc != 0 || $status == 409 ]]; then
                [[ $(owned) == true ]] || { echo 'Account creation unconfirmed. Check before retrying.' >&2; exit 1; }
            elif [[ $status != 201 ]]; then
                echo "Create returned HTTP $status" >&2; exit 1
            fi
        fi
        api "https://sftp.example.net:5443/api/sftpserver/users/import_user" | jq .
        """;

    internal const string RegisterKey = """
        jq -n --rawfile key customer.pub '{publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' |
            api -X PUT --json @- "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
        api "https://sftp.example.net:5443/api/sftpserver/users/import_user" | jq '{authenticationMode,hasPublicKey,enabled}'
        """;

    internal const string ChangeMode = """
        target=PasswordAndPublicKey  # Or PasswordOnly or PublicKeyOnly
        account=$(api "https://sftp.example.net:5443/api/sftpserver/users/import_user")
        current=$(jq -r .authenticationMode <<< "$account")
        if [[ $current != "$target" ]]; then
            if [[ $target != PasswordOnly ]] && jq -e '.hasPublicKey == false' <<< "$account" >/dev/null; then
                jq -n --rawfile key customer.pub '{publicKey:($key | rtrimstr("\n") | rtrimstr("\r"))}' |
                    api -X PUT --json @- "https://sftp.example.net:5443/api/sftpserver/users/import_user/public-key"
            fi
            if [[ $current == PublicKeyOnly ]]; then
                read -rsp 'New customer password: ' password; echo
                printf %s "$password" | jq -Rs --arg mode "$target" '{authenticationMode:$mode,password:.}' > "$work/auth"
                unset password
            else
                jq -n --arg mode "$target" '{authenticationMode:$mode}' > "$work/auth"
            fi
            api -X PUT --json "@$work/auth" "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth"
            rm "$work/auth"
        fi
        api "https://sftp.example.net:5443/api/sftpserver/users/import_user" |
            jq -e --arg mode "$target" '.enabled and .authenticationMode == $mode' >/dev/null
        api "https://sftp.example.net:5443/api/sftpserver/users/import_user/auth-guide" | jq .
        """;

    internal const string Import = """
        delete_after_download=false
        listing=$(api "https://sftp.example.net:5443/api/sftpserver/users/import_user/files")
        if jq -e 'any(.[]; .name == "orders-1042.csv" and .isDirectory == false)' <<< "$listing" >/dev/null; then
            api "https://sftp.example.net:5443/api/sftpserver/users/import_user/files/content?path=orders-1042.csv" -o "$work/content"
            if [[ ! -e orders-1042.csv ]]; then
                (set -o noclobber; cat "$work/content" > orders-1042.csv)
            fi
            cmp "$work/content" orders-1042.csv
            if [[ $delete_after_download == true ]]; then
                api -X DELETE "https://sftp.example.net:5443/api/sftpserver/users/import_user/files?path=orders-1042.csv"
            fi
        fi
        """;

    internal const string Offboard = """
        read -rp 'Action for import_user (disable, enable or DELETE import_user): ' action
        case "$action" in
            disable) api -X PUT "https://sftp.example.net:5443/api/sftpserver/users/import_user/disable" ;;
            enable) api -X PUT "https://sftp.example.net:5443/api/sftpserver/users/import_user/enable" ;;
            'DELETE import_user') api -X DELETE "https://sftp.example.net:5443/api/sftpserver/users/import_user" ;;
            *) echo 'No change made.' ;;
        esac
        """;
}
