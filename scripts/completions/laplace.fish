set -l laplace_commands compress compress-beside estimate extract list info test add freshen delete rename comment lock find diff merge split view repair benchmark open extract-here extract-to-folder extract-to-named-folder extract-dialog iso-to-drive-dialog compress-dialog integrate
set -l laplace_options --json --dry-run --from-file --mode --block-size --solid --threads --verify --no-verify --quiet --encrypt --password --password-file --keyfile --overwrite --name --text --set --file --clear --volume-size --recovery-percent --size --count --cli-path

for cmd in $laplace_commands
    complete -c laplace -f -a $cmd
end

for opt in $laplace_options
    complete -c laplace -f -a $opt
end
