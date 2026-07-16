#requires -Version 7.3
# Shared Wayforge gate shim. Invoked by harness hooks and git hooks.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('pre-tool', 'stop', 'pre-commit', 'pre-push', 'ci')]
    [string] $Stage,
    [string] $AsHook = 'git'
)

$ErrorActionPreference = 'Stop'
$eventJson = if ([Console]::In.Peek() -ge 0) { [Console]::In.ReadToEnd() } else { $null }

Import-Module PSWayforge -ErrorAction Stop

$report = Invoke-WayforgeGate -Stage $Stage -AsHook $AsHook -EventJson $eventJson -ProjectPath (Get-Location).Path
exit $report.ExitCode
