"""Export only a clean committed Git candidate; never copy live worktree bytes."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile


def git(repository, *arguments):
    return subprocess.check_output(['git', '-C', str(repository), *arguments], stderr=subprocess.PIPE)


def forbidden(path):
    parts = PurePosixPath(path).parts
    name = parts[-1].lower()
    return (any(part.lower() in ('.git', '.botnexus', '.ssh', '.aws', '.azure', '.secrets') for part in parts)
            or name in ('auth.json', 'botnexus.local.json', 'config.local.json', '.env', '.npmrc', '.netrc', 'id_rsa', 'id_ed25519', 'credentials.json', 'secrets.json')
            or name.endswith(('.env', '.botnexus-auth', '.pem', '.pfx', '.p12', '.key'))
            or name.startswith('.env.'))


def assert_clean(repository, expected):
    if git(repository, 'rev-parse', 'HEAD').decode().strip() != expected:
        raise ValueError('Candidate HEAD changed or differs from expected commit.')
    if git(repository, 'status', '--porcelain=v1', '--untracked-files=all'):
        raise ValueError('Dirty or untracked candidate input is not approved.')


def digest(root):
    entries = []
    for path in root.rglob('*'):
        if path.is_file():
            relative = path.relative_to(root).as_posix()
            entries.append((relative.encode(), hashlib.sha256(path.read_bytes()).hexdigest()))
    payload = b''.join(value.encode() + b'  ./' + name + b'\n' for name, value in sorted(entries))
    return hashlib.sha256(payload).hexdigest()


def create_snapshot(repository, destination, expected):
    repository, destination = Path(repository).absolute(), Path(destination).absolute()
    if not re.fullmatch('[0-9a-f]{40}', expected):
        raise ValueError('Expected commit must be an exact SHA-1.')
    if destination.exists() or destination.is_symlink():
        raise ValueError('Snapshot destination must not exist.')
    if repository == destination or repository in destination.parents:
        raise ValueError('Snapshot must be outside the repository.')
    for parent in (destination, *destination.parents):
        if parent.name.lower() == '.botnexus':
            raise ValueError('Snapshot must be outside .botnexus.')
        if parent.exists() and (parent.is_symlink() or getattr(parent.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Snapshot parent is a link or reparse point.')
    assert_clean(repository, expected)
    listing = git(repository, 'ls-tree', '-rz', '--full-tree', expected)
    approved = {}
    for entry in listing.split(b'\0'):
        if not entry:
            continue
        header, raw_path = entry.split(b'\t', 1)
        mode, kind, object_id = header.decode().split()
        path = raw_path.decode('utf-8', errors='strict')
        parts = PurePosixPath(path).parts
        if mode not in ('100644', '100755') or kind != 'blob':
            raise ValueError('Tracked links/submodules are not approved: ' + path)
        if '\\' in path or any(ord(char) < 32 for char in path) or path.startswith('/') or '..' in parts or ':' in path:
            raise ValueError('Unsafe tracked source path.')
        if forbidden(path):
            raise ValueError('Tracked secret path is not approved: ' + path)
        # Reject reparse paths in the checkout even though exported bytes come from Git.
        for current in [repository.joinpath(*parts[:index]) for index in range(1, len(parts) + 1)]:
            if current.is_symlink() or (current.exists() and getattr(current.lstat(), 'st_file_attributes', 0) & 0x400):
                raise ValueError('Tracked checkout path is a link/reparse point.')
        approved[path] = (mode, object_id)
    archive = git(repository, '-c', 'core.autocrlf=false', 'archive', '--format=tar', expected)
    try:
        source = destination / 'source'
        source.mkdir(parents=True)
        seen = set()
        with tarfile.open(fileobj=io.BytesIO(archive)) as stream:
            for member in stream:
                if member.isdir():
                    continue
                if not member.isfile() or member.name not in approved or member.name in seen:
                    raise ValueError('Archive differs from approved tracked inventory.')
                target = source / member.name
                target.parent.mkdir(parents=True, exist_ok=True)
                with stream.extractfile(member) as data:
                    content = data.read()
                    blob = hashlib.sha1(b'blob ' + str(len(content)).encode() + b'\0' + content).hexdigest()
                    if blob != approved[member.name][1]:
                        raise ValueError('Archive bytes differ from committed Git blob.')
                    target.write_bytes(content)
                target.chmod(0o755 if approved[member.name][0] == '100755' else 0o644)
                seen.add(member.name)
        if seen != set(approved):
            raise ValueError('Archive omits tracked inputs (export-ignore is not accepted).')
        tree = digest(source)
        # The Docker context has no host .dockerignore semantics: all approved inputs bind the digest.
        (destination / '.dockerignore').write_text('# Exact committed snapshot; no exclusions.\n', encoding='utf-8')
        assert_clean(repository, expected)
        return {'commit': expected, 'tree': tree, 'context': str(destination), 'files': len(seen)}
    except BaseException:
        shutil.rmtree(destination)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--destination', required=True)
    parser.add_argument('--expected-commit', required=True)
    args = parser.parse_args()
    print(json.dumps(create_snapshot(args.repository, args.destination, args.expected_commit)))


if __name__ == '__main__':
    main()
