from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time

SOURCE = Path(__file__).resolve().parent
ROOT = Path(os.environ.get('GIN_IKTHIS_TEST_ROOT', SOURCE)).resolve()
GAME = ROOT / 'studio'
PLUGINS = GAME / 'BepInEx/plugins'
BIN = ROOT / 'bin'
OUT = Path(os.environ.get('GIN_IKTHIS_TEST_OUTPUT', ROOT / 'runtime')).resolve()
NAME = 'GIN IKTHIS Verification.dll'
DLL = BIN / 'GIN IKTHIS.dll'
INDEPENDENT = ('independent', 'shared', 'fk-mixed-shared', 'root-shared', 'root-kohola-shared', 'user-independent')


def build(phase):
    sdk = subprocess.check_output(['dotnet', '--list-sdks'], text=True).splitlines()[-1]
    version, folder = sdk[:-1].split(' [')
    compiler = Path(folder) / version / 'Roslyn/bincore/csc.dll'
    refs = [GAME / 'CharaStudio_Data/Managed' / name for name in (
        'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Xml.dll',
        'UnityEngine.dll', 'UnityEngine.UI.dll', 'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll')]
    refs.append(GAME / 'BepInEx/core/BepInEx.dll')
    refs.append(PLUGINS / 'KKAPI.dll')
    if phase != 'offline':
        refs.append(PLUGINS / 'StudioWorkspaceTest/Timeline.dll')
    if phase not in INDEPENDENT:
        refs.append(DLL)
    dest = BIN / phase / NAME
    dest.parent.mkdir(exist_ok=True)
    args = ['dotnet', str(compiler), '/noconfig', '/nologo', '/nostdlib+', '/langversion:3',
            '/codepage:65001', '/target:library', '/out:' + str(dest)]
    if phase in INDEPENDENT:
        args.append('/define:INDEPENDENT')
    args += ['/reference:' + str(p) for p in refs]
    args.append(str(SOURCE / ('OfflineTest.cs' if phase == 'offline' else 'RuntimeTest.cs')))
    subprocess.run(args, check=True)
    return dest


def run(phase, verifier):
    if not (GAME / '.kk-workshop-test').is_file() or GAME.resolve().parent != ROOT.resolve():
        raise RuntimeError('Marked workspace-only Studio required.')
    OUT.mkdir(exist_ok=True)
    plugin = PLUGINS / 'GINIKTHIS/GIN IKTHIS.dll'
    verify_dest = plugin.with_name(NAME)
    if phase not in INDEPENDENT:
        shutil.copy2(DLL, plugin)
    elif plugin.exists():
        plugin.unlink()
    shutil.copy2(verifier, verify_dest)
    disabled = []
    if phase in ('offline', 'root-no-kkpe'):
        for path in (PLUGINS / 'StudioWorkspaceTest').glob('*.dll'):
            excluded = ('KKPE.dll', 'KK_ShalltyUtils.dll') if phase == 'root-no-kkpe' else (
                'Timeline.dll', 'KKPE.dll', 'KK_ShalltyUtils.dll', 'NodesConstraints.dll')
            if path.name in excluded:
                moved = path.with_suffix('.dll.disabled')
                if moved.exists():
                    raise FileExistsError(moved)
                path.rename(moved)
                disabled.append((moved, path))
    marker = OUT / (phase + '.txt')
    if marker.exists():
        marker.unlink()
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    interactive = phase in ('ui', 'fk-ui', 'root-ui', 'user-ui', 'v1-ui')
    startup.wShowWindow = 1 if interactive else 0
    if interactive:
        for name in ('ui-ready.txt', 'ui-stop.txt'):
            flag = OUT / name
            if flag.exists():
                flag.unlink()
    proc = None
    try:
        proc = subprocess.Popen([str(GAME / 'CharaStudio.exe'), '-screen-width', '1280', '-screen-height', '900',
                                 '-screen-fullscreen', '0', '-ginIkThisVerification', phase, str(OUT),
                                 '-logFile', str(OUT / (phase + '-player.log'))], cwd=GAME, startupinfo=startup)
        print(f'{phase} Studio PID {proc.pid}', flush=True)
        limit = time.monotonic() + (260 if interactive else 160)
        while proc.poll() is None and time.monotonic() < limit and not marker.exists():
            time.sleep(.35)
        if marker.exists() and proc.poll() is None:
            try:
                proc.wait(timeout=7)
            except subprocess.TimeoutExpired:
                pass
    finally:
        if proc is not None and proc.poll() is None:
            proc.terminate()
            proc.wait(timeout=15)
        for current, original in reversed(disabled):
            current.rename(original)
        verify_dest.unlink()
    shutil.copy2(GAME / 'BepInEx/LogOutput.log', OUT / (phase + '-bepinex.log'))
    result = marker.read_text(encoding='utf-8-sig') if marker.exists() else f'FAIL\nMissing result (exit {proc.returncode})'
    print(result, flush=True)
    return result.startswith('PASS\n')


if __name__ == '__main__':
    os.environ['PYTHONIOENCODING'] = 'utf-8'
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    phases = sys.argv[1:] or ['root', 'root-observed', 'root-reload', 'root-shared', 'root-no-kkpe', 'root-kkpe-disabled',
                             'root-kohola', 'root-kohola-shared', 'fk-mixed', 'fk-mixed-shared',
                             'kohola', 'kohola-reload', 'workflow', 'shared', 'main', 'reload', 'independent', 'offline', 'recover']
    report = {'dll_sha256': hashlib.sha256(DLL.read_bytes()).hexdigest(), 'results': {}}
    for phase in phases:
        report['results'][phase] = run(phase, build(phase))
        (OUT / 'matrix.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        if not report['results'][phase]:
            raise SystemExit(1)
