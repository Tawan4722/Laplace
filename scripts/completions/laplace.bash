_laplace_completions()
{
    local cur="${COMP_WORDS[COMP_CWORD]}"
    local commands="compress compress-beside estimate extract list info test add freshen delete rename comment lock find diff merge split view repair benchmark open extract-here extract-to-folder extract-to-named-folder extract-dialog iso-to-drive-dialog compress-dialog integrate"
    local options="--json --dry-run --from-file --mode --block-size --solid --threads --verify --no-verify --quiet --encrypt --password --password-file --keyfile --overwrite --name --text --set --file --clear --volume-size --recovery-percent --size --count --cli-path"
    COMPREPLY=( $(compgen -W "${commands} ${options}" -- "$cur") )
}

complete -F _laplace_completions laplace
