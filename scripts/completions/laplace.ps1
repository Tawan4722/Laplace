Register-ArgumentCompleter -CommandName laplace -ScriptBlock {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)

    $commands = @(
        'compress', 'compress-beside', 'estimate', 'extract', 'list', 'info', 'test',
        'add', 'freshen', 'delete', 'rename', 'comment', 'lock', 'find', 'diff',
        'merge', 'split', 'view', 'repair', 'benchmark', 'open',
        'extract-here', 'extract-to-folder', 'extract-to-named-folder',
        'extract-dialog', 'iso-to-drive-dialog', 'compress-dialog', 'integrate'
    )

    $options = @(
        '--json', '--dry-run', '--from-file', '--mode', '--block-size', '--solid', '--threads',
        '--verify', '--no-verify', '--quiet', '--encrypt', '--password', '--password-file',
        '--overwrite', '--name', '--text', '--set', '--file', '--clear', '--keyfile',
        '--volume-size', '--recovery-percent', '--size', '--count', '--cli-path'
    )

    @($commands + $options) |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
