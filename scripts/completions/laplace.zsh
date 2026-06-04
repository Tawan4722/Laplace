#compdef laplace

local -a commands
local -a options

commands=(
  compress compress-beside estimate extract list info test add freshen delete rename
  comment lock find diff merge split view repair benchmark open extract-here
  extract-to-folder extract-to-named-folder extract-dialog iso-to-drive-dialog
  compress-dialog integrate
)

options=(
  --json --dry-run --from-file --mode --block-size --solid --threads --verify
  --no-verify --quiet --encrypt --password --password-file --keyfile --overwrite --name
  --text --set --file --clear --volume-size --recovery-percent --size --count
  --cli-path
)

_arguments '*:value:->value'

if [[ $state == value ]]; then
  _describe 'laplace' commands
  _describe 'laplace option' options
fi
