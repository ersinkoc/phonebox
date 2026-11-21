"""Copy the language list in i18n.js to the platform settings and README pickers."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT / 'agent/PhoneBox.Agent'
ANDROID = ROOT / 'android/app/src/main'
SOURCE = ANDROID / 'assets/www/i18n.js'


def write(path, contents):
    if not path.exists() or path.read_text(encoding='utf-8') != contents:
        path.write_text(contents, encoding='utf-8', newline='\n')


def replace(path, pattern, replacement):
    contents = path.read_text(encoding='utf-8')
    updated, count = re.subn(pattern, replacement, contents, count=1)
    if count != 1:
        raise SystemExit(f'Could not find the language list in {path.relative_to(ROOT)}')
    write(path, updated)


def main():
    languages = dict(re.findall(r"\{ code: '([\w-]+)', name: '[^']+', nativeName: '([^']+)'",
                                SOURCE.read_text(encoding='utf-8')))
    if not languages or next(iter(languages)) != 'en':
        raise SystemExit('The first language in i18n.js must be English')

    readmes = {'en': ROOT / 'README.md'}
    readmes.update({code: ROOT / f'docs/README.{code}.md' for code in languages if code != 'en'})
    missing = [str(path.relative_to(ROOT)) for path in readmes.values() if not path.is_file()]
    if missing:
        raise SystemExit('Add these README files first: ' + ', '.join(missing))

    csharp_codes = ', '.join(f'"{code}"' for code in ['auto', *languages])
    replace(AGENT / 'Config.cs', r'Languages = \[[^\]]+\];', f'Languages = [{csharp_codes}];')
    cultures = ['zh-Hans' if code == 'zh' else code for code in languages]
    replace(AGENT / 'PhoneBox.Agent.csproj',
            r'(<UseWindowsForms>true</UseWindowsForms>\n    )<SatelliteResourceLanguages>.*?</SatelliteResourceLanguages>',
            lambda match: match[1] + f'<SatelliteResourceLanguages>{";".join(cultures)}</SatelliteResourceLanguages>')

    xml = ET.Element('locale-config', {'xmlns:android': 'http://schemas.android.com/apk/res/android'})
    for code in cultures:
        ET.SubElement(xml, 'locale', {'android:name': code})
    ET.indent(xml, space='    ')
    write(ANDROID / 'res/xml/locales_config.xml',
          '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(xml, encoding='unicode') + '\n')

    for current, path in readmes.items():
        links = []
        for code, name in languages.items():
            if code == current:
                links.append(f'<b>{name}</b>')
                continue
            if current == 'en':
                href = readmes[code].relative_to(ROOT).as_posix()
            elif code == 'en':
                href = '../README.md'
            else:
                href = readmes[code].name
            links.append(f'<a href="{href}">{name}</a>')
        picker = '<p align="center">\n  ' + ' ·\n  '.join(links) + '\n</p>'
        replace(path, r'(<h1[^>]*>PhoneBox</h1>\s*)<p align="center">[\s\S]*?</p>',
                lambda match: match[1] + picker)
    print(f'Synced {len(languages)} locale entries')


if __name__ == '__main__':
    main()
