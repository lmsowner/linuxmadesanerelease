# Copyright (c) Linux Made Sane.
# Licensed under the Business Source License 1.1. See LICENSE for details.
"""Remove only LMS's temporary staging bundle, including root-owned recovery files."""
import os
import pathlib
import re
import shutil
import sys


def cleanup(bundle, base='/dev/shm'):
    path = pathlib.Path(bundle)
    expected = pathlib.Path(base)
    if not path.is_absolute() or path.parent != expected or not re.fullmatch(r'lms-backup-staging-[0-9a-f]{32}', path.name):
        raise ValueError('Refusing cleanup outside an LMS backup staging directory')
    if path.is_symlink():
        raise ValueError('Refusing cleanup of a symlink staging directory')
    if os.path.lexists(path):
        shutil.rmtree(path)


if __name__ == '__main__':
    try:
        cleanup(sys.argv[1])
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
