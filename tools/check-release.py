"""Validate the source metadata and catalog shipped in every release."""
import json,re,pathlib,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1]
version=ET.parse(root/'agent/PhoneBox.Agent/PhoneBox.Agent.csproj').find('.//Version').text
assert re.fullmatch(r'\d+\.\d+\.\d+',version),version
gradle=(root/'android/app/build.gradle.kts').read_text()
assert re.search(r'versionName = "'+re.escape(version)+'"',gradle),'Android version differs'
assert int(re.search(r'versionCode = (\d+)',gradle)[1])>0
changelog=(root/'CHANGELOG.md').read_text(encoding='utf-8')
assert re.search(r'^## '+re.escape(version)+r' — \d{4}-\d{2}-\d{2}$',changelog,re.M),'Missing dated release notes'
widgets=json.loads((root/'agent/PhoneBox.Agent/widgets.json').read_text(encoding='utf-8'))
templates=json.loads((root/'agent/PhoneBox.Agent/templates.json').read_text(encoding='utf-8'))
types={w['type'] for w in widgets}
assert len(types)==len(widgets),'Duplicate widget types'
assert len({t['id'] for t in templates})==len(templates),'Duplicate templates'
for t in templates:
 for w in t['widgets']:
  assert w['type'] in types,(t['id'],w['type'])
  assert 0<=w.get('x',0) and 0<=w.get('y',0) and w['x']+w['w']<=12 and w['y']+w['h']<=6,(t['id'],w)
readme=(root/'README.md').read_text(encoding='utf-8')
import importlib.util,sys
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('pages_build',root/'tools/pages/build.py');build=importlib.util.module_from_spec(spec);spec.loader.exec_module(build)
site=build.landing_html()
assert f'**{len(widgets)} widgets**' in readme and f'**{len(templates)} templates**' in readme,'README catalog counts differ'
assert f'{len(widgets)} widgets, {len(templates)} templates' in site and f'PhoneBox {version}' in site,'Demo catalog counts or version differ'
# the landing page takes its numbers from the sources; a number typed by hand would go stale
assert not re.search(r'\d+ (widgets|templates)',(root/'tools/pages/index.html').read_text(encoding='utf-8')),'Hard-coded catalog count in the landing page'
demo=(root/'tools/pages/demo.js').read_text(encoding='utf-8')
pages=json.loads(re.search(r'const PAGES = (\[[^\]]*\])',demo)[1].replace("'",'"'))
assert sorted(pages)==sorted(t['id'] for t in templates),'The demo does not show every template exactly once'
print(f'PASS: {version}; Android metadata; {len(widgets)} widgets; {len(templates)} templates; README/demo counts')


def check_languages():
    www = root / 'android/app/src/main/assets/www'
    res = root / 'android/app/src/main/res'
    agent = root / 'agent/PhoneBox.Agent'
    source = (www / 'i18n.js').read_text(encoding='utf-8')
    codes = set(re.findall(r"\{ code: '([\w-]+)'", source))
    assert 'en' in codes
    config = (agent / 'Config.cs').read_text(encoding='utf-8')
    saved_codes = re.search(r'Languages = \[([^\]]+)\]', config)[1]
    assert set(re.findall(r'"([\w-]+)"', saved_codes)) == codes | {'auto'}
    culture = {code: 'zh-Hans' if code == 'zh' else code for code in codes}
    android_config = ET.parse(res / 'xml/locales_config.xml').getroot()
    assert {n.attrib['{http://schemas.android.com/apk/res/android}name'] for n in android_config} == set(culture.values())
    csproj = (agent / 'PhoneBox.Agent.csproj').read_text(encoding='utf-8')
    windows_codes = re.findall(r'<SatelliteResourceLanguages>(.*?)</SatelliteResourceLanguages>', csproj)[-1]
    assert set(windows_codes.split(';')) == set(culture.values())

    def values(file, element, child=None):
        return {n.attrib['name']: n.text if child is None else n.findtext(child)
                for n in ET.parse(file).findall(element)}

    def check_placeholders(english, translated, pattern, code):
        assert english.keys() == translated.keys(), (code, english.keys() - translated.keys())
        for key, text in english.items():
            value = translated[key]
            assert value and value.strip(), (code, key)
            assert sorted(re.findall(pattern, text)) == sorted(re.findall(pattern, value)), (code, key)

    android = values(res / 'values/strings.xml', 'string')
    windows = values(agent / 'TrayStrings.resx', 'data', 'value')
    android_folders = {'id': 'in', 'pt-BR': 'b+pt+BR', 'zh-Hant': 'zh-rTW'}
    for code in sorted(codes - {'en'}):
        folder = android_folders.get(code, code)
        android_strings = values(res / f'values-{folder}/strings.xml', 'string')
        tray_strings = values(agent / f'TrayStrings.{culture[code]}.resx', 'data', 'value')
        check_placeholders(android, android_strings, r'%[0-9]+\$[sd]', code)
        check_placeholders(windows, tray_strings, r'\{[0-9]+\}', code)

    packs = {p.name for p in www.glob('locale-*.js')}
    assert packs
    for page in (www / 'index.html', agent / 'settings.html'):
        loaded = re.findall(r'<script src="(?:\.\./panel/)?(locale-[\w-]+\.js)">', page.read_text(encoding='utf-8'))
        assert len(loaded) == len(set(loaded)) and set(loaded) == packs, page

    # Machine-translated packs once carried English left-overs and raw bytes (#358): "ясне" + "weather condition",
    # "冰<0xE9><0x9B><0xB9>", "синій" + "color", and empty values where English has text.
    english = {}
    for m in re.finditer(r"^\s*'([\w.\-]+)':\s*'((?:[^'\\]|\\.)*)',?\s*$", source, re.M):
        english.setdefault(m.group(1), m.group(2))
    leftovers = re.compile(r'weather (?:condition|forecast)|<0x[0-9A-Fa-f]{2}>'
                           r'|[Ѐ-ӿऀ-ॿ฀-๿가-힯][a-z]{3,}')
    parsed = {}
    for name in sorted(packs):
        text = (www / name).read_text(encoding='utf-8')
        start = text.index('{', text.index('{', text.index('Object.assign')) + 1)
        parsed[name] = json.loads(text[start:text.rindex('}') + 1])
        english.update(parsed[name].get('en', {}).get('strings', {}))  # some English strings live in a pack
    for name, languages in parsed.items():
        for code, pack in languages.items():
            for section_name, section in pack.items():
                for key, value in (section.items() if isinstance(section, dict) else []):
                    if isinstance(value, str):
                        assert not leftovers.search(value), (name, code, key, value)
                        # catalog and panel entries are keyed by their English text, and an empty translation
                        # is shown as is, without falling back to English
                        source_text = english.get(key) if section_name == 'strings' else key
                        assert value or not source_text, (name, code, key, 'empty')

    readmes = {'en': root / 'README.md'}
    readmes.update({code: root / f'docs/README.{code}.md' for code in codes - {'en'}})
    assert set((root / 'docs').glob('README.*.md')) == set(readmes.values()) - {readmes['en']}
    for code, path in readmes.items():
        content = path.read_text(encoding='utf-8')
        first_links = re.search(r'<h1[^>]*>PhoneBox</h1>\s*<p[^>]*>(.*?)</p>', content, re.S)[1]
        links = {(path.parent / href).resolve() for href in re.findall(r'href="([^"]+)"', first_links)}
        assert links == set(readmes.values()) - {path}, (code, links)
    print(f'PASS: {len(codes)} interface variants; Android, Windows, and README language lists and native placeholders')


check_languages()
