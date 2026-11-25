# Compatibility: -SkipBuild is accepted, but ordinary start never builds.
param([switch]$SkipBuild)
. (Join-Path $PSScriptRoot 'Runtime.ps1')
Start-HagoromoInstalled (Get-HagoromoRoot)
