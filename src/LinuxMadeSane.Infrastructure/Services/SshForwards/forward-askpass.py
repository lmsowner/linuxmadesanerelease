#!/usr/bin/python3
# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.
import os, socket, sys
with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
    client.settimeout(5)
    client.connect(os.environ['LMS_SSH_ASKPASS_SOCKET'])
    client.sendall((' '.join(sys.argv[1:])).encode())
    answer = client.recv(65536)
    if not answer:
        sys.exit(1)
    sys.stdout.buffer.write(answer + b'\n')
