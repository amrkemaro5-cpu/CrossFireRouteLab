from pathlib import Path
from types import SimpleNamespace
import sys

sys.path.insert(0, str(Path(__file__).parent))

# The packaged project loads cf_rez_original from the original executable's PYZ.
# These tests are intended to run in the extracted development environment.
import cf_rez_learned


def test_png_signature():
    r = cf_rez_learned.analyze_resource('test.png', b'\x89PNG\r\n\x1a\nabc')
    assert r['signature'] == 'PNG image'


def test_dat_is_conservative():
    r = cf_rez_learned.analyze_resource('AI_TEST.DAT', b'CrossFire DAT ' + bytes(range(64)))
    assert r['extension'] == 'DAT'
    assert 'binary structure candidate' in r['family']
    assert r['confidence'] == 'structural only'


def test_cft_probe():
    class FakeCore:
        @staticmethod
        def parse_cft(_data):
            return {'columns': ['A', 'B'], 'rows': [[1, 2]]}

    r = cf_rez_learned.analyze_resource('AICHARACTER.CFT', b'su' + b'\x00' * 32, FakeCore)
    assert r['cft']['ok']
    assert r['cft']['rows'] == 1
    assert r['cft']['columns'] == 2


if __name__ == '__main__':
    test_png_signature()
    test_dat_is_conservative()
    test_cft_probe()
    print('CF REZ overlay analyzer tests: PASS')
