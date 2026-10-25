#!/usr/bin/env python3
"""Report actual test outcomes and unsupported code; an inventory is not a correctness proof."""
import argparse
import json
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--audit')
parser.add_argument('--trx')
parser.add_argument('--inventory')
args = parser.parse_args()
if args.audit:
    data = json.loads(pathlib.Path(args.audit).read_text())
    if data.get('problems'):
        raise SystemExit('NuGet audit did not complete: ' + json.dumps(data['problems']))
    vulnerable = {}
    for project in data.get('projects', []):
        for framework in project.get('frameworks', []):
            for package in framework.get('topLevelPackages', []) + framework.get('transitivePackages', []):
                for finding in package.get('vulnerabilities', []):
                    vulnerable[(package['id'], finding['advisoryurl'])] = finding['severity']
    print('NuGet advisories:', len(vulnerable))
    if vulnerable:
        print(json.dumps([{'package': k[0], 'advisory': k[1], 'severity': v} for k, v in vulnerable.items()], indent=2))
        raise SystemExit('Dependency advisories require review; the audit does not silently suppress them.')
if args.trx:
    result = ET.parse(args.trx)
    namespace = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    counters = result.find('.//t:ResultSummary/t:Counters', namespace)
    if counters is None or int(counters.get('executed', '0')) == 0:
        raise SystemExit('No tests executed: ' + args.trx)
    print(pathlib.Path(args.trx).stem, json.dumps(counters.attrib, sort_keys=True))
    if int(counters.get('failed', '0')) or int(counters.get('error', '0')) or int(counters.get('timeout', '0')):
        raise SystemExit('Tests did not pass: ' + args.trx)
if args.inventory:
    entries = []
    pattern = re.compile(r'NotImplementedException|NotSupportedException|\bTODO\b|\bFIXME\b|\bSTUB\b')
    # Tracked files plus new source files in the current working tree; no caches or generated code.
    names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard'], text=True).splitlines()
    for name in sorted(set(names)):
        if not name.endswith('.cs') or not name.startswith(('Source/', 'Addons/')):
            continue
        for number, line in enumerate(pathlib.Path(name).read_text(errors='replace').splitlines(), 1):
            if pattern.search(line):
                entries.append({'file': name, 'line': number, 'text': line.strip()})
    pathlib.Path(args.inventory).write_text(json.dumps(entries, indent=2) + '\n')
    print('Implementation markers requiring contextual review:', len(entries))
