"""Dump Qt .qm translation files to TSV (context, source, translation).

Usage: python qm_dump.py <file.qm> [...] > out.tsv
"""
import struct
import sys

QM_MAGIC = bytes([0x3C, 0xB8, 0x64, 0x18, 0xCA, 0xEF, 0x9C, 0x95,
                  0xCD, 0x21, 0x1C, 0xBF, 0x60, 0xA1, 0xBD, 0xDD])
SECTION_MESSAGES = 0x69


def read_messages(blob):
    pos = 0
    out = []
    while pos < len(blob):
        context = source = comment = ""
        translations = []
        while pos < len(blob):
            tag = blob[pos]
            pos += 1
            if tag == 1:  # end of message
                break
            if tag == 3:  # translation, UTF-16BE
                (n,) = struct.unpack(">I", blob[pos:pos + 4])
                pos += 4
                if n == 0xFFFFFFFF:
                    translations.append("")
                    continue
                translations.append(blob[pos:pos + n].decode("utf-16-be", "replace"))
                pos += n
            elif tag in (6, 7, 8):  # source, context, comment (8-bit)
                (n,) = struct.unpack(">I", blob[pos:pos + 4])
                pos += 4
                text = blob[pos:pos + n].decode("utf-8", "replace").rstrip("\x00")
                pos += n
                if tag == 6:
                    source = text
                elif tag == 7:
                    context = text
                else:
                    comment = text
            elif tag == 5:  # obsolete hash
                pos += 4
            else:
                raise ValueError(f"unknown tag {tag} at {pos - 1}")
        out.append((context, source, " | ".join(translations)))
    return out


def dump(path):
    data = open(path, "rb").read()
    if not data.startswith(QM_MAGIC):
        raise ValueError(f"{path}: not a .qm file")
    pos = len(QM_MAGIC)
    while pos < len(data):
        tag = data[pos]
        (length,) = struct.unpack(">I", data[pos + 1:pos + 5])
        pos += 5
        if tag == SECTION_MESSAGES:
            yield from read_messages(data[pos:pos + length])
        pos += length


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    for p in sys.argv[1:]:
        for ctx, src, tr in dump(p):
            clean = lambda s: s.replace("\t", " ").replace("\n", "\\n")
            print(f"{clean(ctx)}\t{clean(src)}\t{clean(tr)}")
