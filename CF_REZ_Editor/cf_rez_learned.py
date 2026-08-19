import hashlib, math, re, struct, lzma
from collections import Counter

PRINT_RE = re.compile(rb'[\x20-\x7e]{4,}')


def _entropy(data):
    if not data:
        return 0.0
    c = Counter(data)
    n = len(data)
    return -sum((v / n) * math.log2(v / n) for v in c.values())


def _strings(data, limit=16):
    out = []
    for m in PRINT_RE.finditer(data):
        out.append(m.group().decode('ascii', 'replace'))
        if len(out) >= limit:
            break
    return out


def _utf16_strings(data, limit=16):
    out = []
    for m in re.finditer(rb'(?:[\x20-\x7e]\x00){4,}', data):
        try:
            out.append(m.group().decode('utf-16le', 'replace').rstrip('\x00'))
        except Exception:
            pass
        if len(out) >= limit:
            break
    return out


def _sig(data, ext):
    if data.startswith(b'\x89PNG\r\n\x1a\n'):
        return 'PNG image'
    if data.startswith(b'\xff\xd8\xff'):
        return 'JPEG image'
    if data.startswith(b'BM'):
        return 'BMP image'
    if data.startswith(b'DDS '):
        return 'DDS texture'
    if data.startswith(b'OggS'):
        return 'Ogg audio'
    if data.startswith(b'RIFF') and len(data) >= 12 and data[8:12] == b'WAVE':
        return 'WAV audio'
    if data.startswith(b'PK\x03\x04'):
        return 'ZIP container'
    if data.startswith(b'\x1f\x8b'):
        return 'GZIP stream'
    if data.startswith(b'SQLite format 3\x00'):
        return 'SQLite database'
    if data.startswith(b'<?xml') or data.lstrip().startswith(b'<?xml'):
        return 'XML text'
    if data.lstrip().startswith((b'{', b'[')):
        return 'JSON-like text'
    if data.startswith(b'su') and (ext.upper() == 'CFT' or b'\x00\x00' in data[:64]):
        return 'CFT-like'
    return 'Unknown binary/text'


def _integer_profile(data):
    n = min(len(data) // 4, 4096)
    if not n:
        return {'le_small': 0, 'be_small': 0, 'zero_words': 0, 'sample_le': []}
    le = []
    be = []
    for i in range(n):
        off = i * 4
        le.append(struct.unpack_from('<I', data, off)[0])
        be.append(struct.unpack_from('>I', data, off)[0])
    return {
        'le_small': sum(v < 0x100000 for v in le),
        'be_small': sum(v < 0x100000 for v in be),
        'zero_words': sum(v == 0 for v in le),
        'sample_le': le[:12],
    }


def _cft_probe(data, core):
    try:
        doc = core.parse_cft(data)
        if isinstance(doc, dict):
            cols = doc.get('columns', [])
            rows = doc.get('rows', [])
        elif isinstance(doc, tuple) and len(doc) >= 3:
            cols, _types, rows = doc[:3]
        else:
            raise ValueError('Unsupported CFT parser result')
        return {
            'ok': True,
            'columns': len(cols),
            'rows': len(rows),
            'column_names': [str(x) for x in cols[:20]],
        }
    except Exception as e:
        return {'ok': False, 'error': str(e)[:180]}


def analyze_resource(path, data, core=None):
    ext = path.rsplit('.', 1)[-1].upper() if '.' in path else ''
    result = {
        'extension': ext,
        'stored_size': len(data),
        'sha256': hashlib.sha256(data).hexdigest(),
        'md5': hashlib.md5(data).hexdigest(),
        'entropy': round(_entropy(data), 4),
        'signature': _sig(data, ext),
        'ascii_strings': _strings(data),
        'utf16_strings': _utf16_strings(data),
        'integer_profile': _integer_profile(data),
        'lzma': False,
        'decoded_size': None,
    }
    decoded = data
    try:
        decoded = lzma.decompress(data, format=lzma.FORMAT_ALONE)
        result['lzma'] = True
        result['decoded_size'] = len(decoded)
        result['decoded_signature'] = _sig(decoded, ext)
    except Exception:
        pass

    if core is not None and (ext == 'CFT' or data.startswith(b'su') or decoded.startswith(b'su')):
        result['cft'] = _cft_probe(decoded, core)
    else:
        result['cft'] = None

    if result['cft'] and result['cft'].get('ok'):
        family = 'CFT (decoded/validated)'
    elif result['lzma']:
        family = f'{ext or "Binary"} with LZMA-Alone wrapper'
    elif result['signature'] != 'Unknown binary/text':
        family = result['signature']
    elif ext in {'DAT', 'UTC', 'LTC', 'LTA', 'LTB', 'DTX', 'MSZ', 'NUT', 'ENC', 'CFG'}:
        family = f'{ext} — binary structure candidate'
    elif ext:
        family = f'{ext} — unclassified'
    else:
        family = 'Extensionless binary'

    result['family'] = family
    result['confidence'] = (
        'high' if result['cft'] and result['cft'].get('ok')
        else 'medium' if result['lzma'] or result['signature'] != 'Unknown binary/text'
        else 'structural only'
    )
    return result


def format_report(path, data, core=None):
    r = analyze_resource(path, data, core)
    lines = [
        f"Format family: {r['family']}",
        f"Confidence: {r['confidence']}",
        f"Stored size: {r['stored_size']:,} bytes",
        f"Entropy: {r['entropy']:.4f} bits/byte",
        f"Signature: {r['signature']}",
        f"LZMA-Alone: {'YES' if r['lzma'] else 'NO'}",
    ]
    if r['decoded_size'] is not None:
        lines.append(f"Decoded size: {r['decoded_size']:,} bytes")
    if r.get('cft'):
        c = r['cft']
        lines.append(f"CFT parser: {'PASS' if c.get('ok') else 'not validated'}")
        if c.get('ok'):
            lines.append(f"CFT dimensions: {c['rows']:,} rows × {c['columns']:,} columns")
            if c.get('column_names'):
                lines.append('CFT columns: ' + ', '.join(c['column_names']))
    ip = r['integer_profile']
    lines.append(f"32-bit LE small-value words: {ip['le_small']:,}; zero words: {ip['zero_words']:,}")
    if r['ascii_strings']:
        lines.append('ASCII strings: ' + ' | '.join(r['ascii_strings'][:8]))
    if r['utf16_strings']:
        lines.append('UTF-16 strings: ' + ' | '.join(r['utf16_strings'][:8]))
    if r['extension'] in {'DAT', 'UTC'}:
        lines.append(f"{r['extension']} note: no guessed schema is applied; structural evidence is shown for comparison/reverse engineering.")
    return '\n'.join(lines)
