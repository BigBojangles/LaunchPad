"""Read allocated guest files without booting, mounting or replaying the journal."""
import hashlib
import posixpath
import stat
from pathlib import Path

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(data)
    return h.hexdigest()

def open_filesystem(image):
    from dissect.hypervisor.disk.qcow2 import QCow2
    from dissect.volume.disk import Disk
    from dissect.extfs.extfs import ExtFS
    disk = Disk(QCow2(Path(image)).open())
    for part in disk.partitions:
        try:
            fs = ExtFS(part.open())
        except Exception:
            continue
        try:
            fs.get('/etc/debian_version')
        except Exception:
            continue
        return fs
    raise ValueError('No Debian root filesystem found')

def walk(fs):
    pending = [('/', fs.root)]
    while pending:
        path, node = pending.pop()
        yield path, node
        if stat.S_ISDIR(node.filetype):
            for child in node.iterdir():
                if child.filename not in ('.', '..'):
                    pending.append((posixpath.join(path, child.filename), child))

def resolve(fs, path):
    for _ in range(20):
        node = fs.get(path)
        if not stat.S_ISLNK(node.filetype):
            return node
        path = posixpath.normpath(node.link if node.link.startswith('/') else
                                  posixpath.join(posixpath.dirname(path), node.link))
    raise ValueError('Guest symlink loop')

def fingerprint(node):
    if stat.S_ISLNK(node.filetype):
        return {'type': 'symlink', 'link': node.link}
    if stat.S_ISDIR(node.filetype):
        return {'type': 'directory'}
    if not stat.S_ISREG(node.filetype):
        return {'type': 'special'}
    h = hashlib.sha256()
    stream = node.open()
    for data in iter(lambda: stream.read(1024 * 1024), b''):
        h.update(data)
    return {'type': 'file', 'sha256': h.hexdigest()}
