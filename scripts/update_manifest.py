#!/usr/bin/env python3
"""Write a released version into manifest.json (checksum, timestamp, download url).

Usage:
    python3 scripts/update_manifest.py --version 1.0.0.0 --checksum <md5> [--owner-repo owner/repo]

--owner-repo defaults to $GITHUB_REPOSITORY, otherwise the owner/repo already present
in manifest.json (sourceUrl) is kept.
"""
import argparse
import datetime
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST = os.path.join(ROOT, 'manifest.json')
PLUGIN_ASSET = 'Jellyfin.Plugin.FeatureEnhance_{version}.zip'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', required=True)
    ap.add_argument('--checksum', required=True)
    ap.add_argument('--owner-repo', default=os.environ.get('GITHUB_REPOSITORY', ''))
    ap.add_argument('--changelog', default='')
    args = ap.parse_args()

    with open(MANIFEST, encoding='utf-8') as fh:
        data = json.load(fh)

    plugin = data[0]
    owner_repo = args.owner_repo
    if not owner_repo:
        m = re.match(r'https://github\.com/([^/]+)/([^/]+)', plugin.get('sourceUrl', ''))
        owner_repo = f'{m.group(1)}/{m.group(2)}' if m else '<OWNER>/<REPO>'

    owner = owner_repo.split('/')[0]
    asset = PLUGIN_ASSET.format(version=args.version)
    entry = {
        'version': args.version,
        'changelog': args.changelog or f'Release {args.version}',
        'targetAbi': plugin['versions'][0].get('targetAbi', '12.0.0.0'),
        'sourceUrl': f'https://github.com/{owner_repo}',
        'checksum': args.checksum,
        'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'downloadUrl': f'https://github.com/{owner_repo}/releases/download/v{args.version}/{asset}',
    }

    versions = [v for v in plugin['versions'] if v['version'] != args.version]
    versions.insert(0, entry)
    plugin['versions'] = versions
    plugin['owner'] = owner
    plugin['sourceUrl'] = f'https://github.com/{owner_repo}'
    plugin['imageUrl'] = f'https://raw.githubusercontent.com/{owner_repo}/main/docs/icon.png'

    with open(MANIFEST, 'w', encoding='utf-8') as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)
        fh.write('\n')

    print(f'manifest.json updated: {args.version} checksum={args.checksum}')


if __name__ == '__main__':
    sys.exit(main())
