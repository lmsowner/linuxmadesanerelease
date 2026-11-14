// Copyright (c) 2026 LMS Developer. All rights reserved.
// Applicable licence and release scope: see LICENSE for details.
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <linux/landlock.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define POLICY "/.lms-sftp-policy"
#define SERVER "/usr/local/libexec/lms-sftp-server"
#ifndef LANDLOCK_ACCESS_FS_TRUNCATE
#define LANDLOCK_ACCESS_FS_TRUNCATE (1ULL << 14)
#endif
static void fail(const char *message) { fprintf(stderr, "LMS SFTP permissions: %s (%s)\n", message, strerror(errno)); exit(1); }
static void rule(int ruleset, const char *path, uint64_t access, int optional) {
    int fd = open(path, O_PATH | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) { if (optional && errno == ENOENT) return; fail("cannot open policy path"); }
    struct landlock_path_beneath_attr attr = { .allowed_access = access, .parent_fd = fd };
    if (syscall(SYS_landlock_add_rule, ruleset, LANDLOCK_RULE_PATH_BENEATH, &attr, 0)) fail("cannot install filesystem rule");
    close(fd);
}
int main(int argc, char **argv) {
    int abi = syscall(SYS_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
    if (abi < 3) { errno = ENOTSUP; fail("Landlock ABI 3 or newer is required; access was refused"); }
    if (argc == 2 && !strcmp(argv[1], "--probe")) { printf("Landlock ABI %d\n", abi); return 0; }
    int policy = open(POLICY, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    struct stat st;
    if (policy < 0 || fstat(policy, &st) || !S_ISREG(st.st_mode) || st.st_uid != 0 || (st.st_mode & 022)) fail("policy must be a root-owned, protected regular file");
    char text[32] = {0}; ssize_t size = read(policy, text, sizeof(text)-1); close(policy);
    if (size <= 0) fail("cannot read policy");
    char *end; unsigned long mask = strtoul(text, &end, 10);
    while (*end == '\n' || *end == ' ' || *end == '\r') end++;
    if (*end || mask > 127) { errno = EINVAL; fail("invalid permissions"); }
    uint64_t handled = (1ULL << 15) - 1;
    struct landlock_ruleset_attr attr = { .handled_access_fs = handled };
    int ruleset = syscall(SYS_landlock_create_ruleset, &attr, sizeof(attr), 0);
    if (ruleset < 0) fail("cannot create filesystem policy");
    rule(ruleset, "/", LANDLOCK_ACCESS_FS_READ_DIR, 0);
    rule(ruleset, SERVER, LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_EXECUTE, 0);
    rule(ruleset, "/lib", LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_EXECUTE, 1);
    rule(ruleset, "/lib64", LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_EXECUTE, 1);
    rule(ruleset, "/usr/lib", LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_EXECUTE, 1);
    rule(ruleset, "/etc/passwd", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    rule(ruleset, "/etc/group", LANDLOCK_ACCESS_FS_READ_FILE, 0);
    rule(ruleset, "/dev/null", LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_WRITE_FILE, 0);
    uint64_t access = LANDLOCK_ACCESS_FS_READ_DIR;
    if (mask & 1) access |= LANDLOCK_ACCESS_FS_READ_FILE;
    if (mask & 2) access |= LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_TRUNCATE;
    if (mask & 4) access |= LANDLOCK_ACCESS_FS_MAKE_REG;
    if (mask & 8) access |= LANDLOCK_ACCESS_FS_REMOVE_FILE;
    if (mask & 16) access |= LANDLOCK_ACCESS_FS_MAKE_DIR;
    if (mask & 32) access |= LANDLOCK_ACCESS_FS_REMOVE_DIR;
    if (mask & 64) access |= LANDLOCK_ACCESS_FS_REFER;
    rule(ruleset, "/files", access, 0);
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) || syscall(SYS_landlock_restrict_self, ruleset, 0)) fail("cannot enforce filesystem policy");
    close(ruleset);
    char *args[] = { SERVER, "-d", "/files", "-e", "-u", "0027", "-P", "rename,posix-rename", NULL };
    if (mask & 64) args[6] = NULL;
    char *env[] = { "PATH=/usr/bin:/bin", "LANG=C", NULL };
    execve(SERVER, args, env);
    fail("cannot start the SFTP subsystem");
}
