# Minimal WinHarness RPC client: sends a prompt, prints assistant text,
# and shuts down. Usage: .\rpc-client.ps1 "your prompt"
param([string]$Prompt = "Say hello in one sentence.")

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "winharness"
$psi.Arguments = "rpc"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false
$proc = [System.Diagnostics.Process]::Start($psi)

$request = @{ id = "p1"; method = "prompt"; prompt = $Prompt } | ConvertTo-Json -Compress
$proc.StandardInput.WriteLine($request)

while (-not $proc.StandardOutput.EndOfStream) {
    $record = $proc.StandardOutput.ReadLine() | ConvertFrom-Json
    if ($record.kind -eq "event") {
        switch ($record.event.type) {
            "assistant_delta" { Write-Host -NoNewline $record.event.text }
            "error"           { Write-Error $record.event.error }
            { $_ -in "turn_end", "error" } {
                $proc.StandardInput.WriteLine('{"id":"s1","method":"shutdown"}')
            }
        }
    }
    elseif ($record.kind -eq "response" -and $record.id -eq "s1") { break }
}

Write-Host ""
$proc.WaitForExit()
