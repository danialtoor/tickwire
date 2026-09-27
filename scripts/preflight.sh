#!/usr/bin/env bash
# Checks the tools and logins the build and deploy steps need. Prints the fix for anything missing.
set -uo pipefail
missing=0
check() {
  local name="$1" cmd="$2" fix="$3"
  if out="$(eval "$cmd" 2>&1)"; then
    printf '  ok   %-14s %s\n' "$name" "$(echo "$out" | head -1)"
  else
    printf '  MISS %-14s -> %s\n' "$name" "$fix"
    missing=1
  fi
}
echo "Tools"
check dotnet   'dotnet --version'           'install the .NET 10 SDK: https://dot.net'
check node     'node -v'                    'install Node 22+: https://nodejs.org'
check python3  'python3 --version'          'install Python 3.12+'
check docker   'docker info --format "{{.ServerVersion}}"' 'install and start Docker Desktop'
check pwsh     'pwsh -v'                    'brew install --cask powershell'
check gh       'gh --version'               'brew install gh'
check vercel   'vercel --version'           'npm i -g vercel'
check flyctl   'fly version'                'brew install flyctl'
echo "Logins"
check github   'gh auth status'             'gh auth login --scopes repo,workflow,project'
check vercel   'vercel whoami'              'vercel login'
check fly      'fly auth whoami'            'fly auth login'
exit $missing
