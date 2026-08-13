$ErrorActionPreference='Stop'
$root='Q:\repos\botnexus-wt\fix-2758-skill-wrapper-hint\artifacts\azure-buildtest\20260813041735-6b7d812d'
Get-ChildItem -Recurse -Filter *.trx $root | ForEach-Object {
  [xml]$x = Get-Content $_.FullName -Raw
  $ns = New-Object Xml.XmlNamespaceManager $x.NameTable
  $ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
  $x.SelectNodes('//t:UnitTestResult[@outcome="Failed"]',$ns) | ForEach-Object { $_.testName }
} | Sort-Object -Unique
