# Ermittelt die nächste Version: letzter Git-Tag vX.Y.Z mit um 1 erhöhtem Patch.
# Steht in der csproj eine höhere Version (zum Beispiel 1.0.0), gilt diese. So lässt sich gezielt springen.

$project = Join-Path $PSScriptRoot '..\HeadsetShare\HeadsetShare.csproj'
$baseVersion = [version]([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version

$latestTag = git tag --list 'v*' |
    Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
    ForEach-Object { [version]$_.Substring(1) } |
    Sort-Object -Descending |
    Select-Object -First 1

if ($null -eq $latestTag) {
    $next = $baseVersion
} else {
    $next = [version]::new($latestTag.Major, $latestTag.Minor, $latestTag.Build + 1)
    if ($baseVersion -gt $next) {
        $next = $baseVersion
    }
}

Write-Output $next.ToString(3)
