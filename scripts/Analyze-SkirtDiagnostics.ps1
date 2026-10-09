[CmdletBinding()]
param(
    # A diagnostics directory or one JSON report. Input is never modified.
    [Parameter(Position = 0)]
    [string]$Path = "$env:APPDATA\XIVLauncherCN\pluginConfigs\FFMMD\diagnostics",

    # Defaults beside this script's repository checkout. The output is a new file.
    [string]$OutputPath = (Join-Path (Join-Path $PSScriptRoot '..\.build') 'skirt-diagnostics-summary.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PropertyValue {
    param([object]$Object, [string]$Name, [object]$Default = $null)
    if ($null -eq $Object) { return $Default }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name) -and $null -ne $Object[$Name]) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Default }
    return $property.Value
}

function As-Array {
    param([object]$Value)
    if ($null -eq $Value) { return ,@() }
    return ,@($Value)
}

function As-Number {
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    try { return [double]$Value } catch { return $null }
}

function As-FloatArray {
    param([object]$Value)
    $values = As-Array $Value
    if ($values.Count -eq 0) { return $null }
    $result = [System.Collections.Generic.List[double]]::new()
    foreach ($value in $values) {
        $number = As-Number $value
        if ($null -eq $number -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
        $result.Add($number)
    }
    return ,$result.ToArray()
}

function Quaternion-AngleDegrees {
    param([object]$Left, [object]$Right)
    # q and -q represent the same rotation: use abs(dot) before acos.
    $a = As-FloatArray $Left
    $b = As-FloatArray $Right
    if ($null -eq $a -or $null -eq $b -or $a.Length -lt 4 -or $b.Length -lt 4) { return $null }
    $la = [math]::Sqrt(($a[0] * $a[0]) + ($a[1] * $a[1]) + ($a[2] * $a[2]) + ($a[3] * $a[3]))
    $lb = [math]::Sqrt(($b[0] * $b[0]) + ($b[1] * $b[1]) + ($b[2] * $b[2]) + ($b[3] * $b[3]))
    if ($la -lt 1e-12 -or $lb -lt 1e-12) { return $null }
    $dot = (($a[0] * $b[0]) + ($a[1] * $b[1]) + ($a[2] * $b[2]) + ($a[3] * $b[3])) / ($la * $lb)
    $dot = [math]::Min(1.0, [math]::Max(-1.0, [math]::Abs($dot)))
    return 2.0 * [math]::Acos($dot) * 180.0 / [math]::PI
}

function Is-TrueOne {
    param([object]$Value)
    return $null -ne $Value -and [int]$Value -eq 1
}

function Is-UsableLocalPose {
    param([object]$Bone)
    # LocalInSync=0 means the raw local cache is dirty. Do not treat its
    # quaternion as an authoritative pose or include it in qangle comparisons.
    $status = [string](Get-PropertyValue $Bone 'Status' 'Unknown')
    $flags = As-Number (Get-PropertyValue $Bone 'BoneFlags')
    return $null -ne $Bone -and $status -eq 'Observed' -and
        (Is-TrueOne (Get-PropertyValue $Bone 'LocalInSync')) -and
        ($null -eq $flags -or ([long]$flags -band 1) -eq 0) -and
        $null -ne (As-FloatArray (Get-PropertyValue $Bone 'RawLocalRotation'))
}

function Is-UsableModelPose {
    param([object]$Bone)
    $status = [string](Get-PropertyValue $Bone 'Status' 'Unknown')
    $flags = As-Number (Get-PropertyValue $Bone 'BoneFlags')
    return $null -ne $Bone -and $status -eq 'Observed' -and
        (Is-TrueOne (Get-PropertyValue $Bone 'ModelInSync')) -and
        ($null -eq $flags -or ([long]$flags -band 2) -eq 0) -and
        $null -ne (As-FloatArray (Get-PropertyValue $Bone 'RawModelRotation'))
}

function Get-BoneMap {
    param([object]$Stage)
    $map = @{}
    foreach ($bone in (As-Array (Get-PropertyValue $Stage 'SkirtBones'))) {
        $name = [string](Get-PropertyValue $bone 'BoneName' '')
        $partial = Get-PropertyValue $bone 'PartialSkeletonIndex' -1
        $index = Get-PropertyValue $bone 'BoneIndex' -1
        if (-not [string]::IsNullOrWhiteSpace($name)) { $map["${partial}:${index}:$name"] = $bone }
    }
    return $map
}

function Get-MaxAngle {
    param([object[]]$Values)
    $valid = @($Values | Where-Object { $null -ne $_.AngleDegrees })
    if ($valid.Count -eq 0) { return $null }
    return ($valid | Sort-Object AngleDegrees -Descending | Select-Object -First 1)
}

function Get-ResourceGroupSummary {
    param([object]$Stage)
    $groups = [System.Collections.Generic.List[object]]::new()
    foreach ($slot in (As-Array (Get-PropertyValue $Stage 'ResourceSlots'))) {
        $resourceIndex = Get-PropertyValue $slot 'ResourceIndex' -1
        $simulators = As-Array (Get-PropertyValue $slot 'Simulators')
        $collisions = As-Array (Get-PropertyValue $slot 'Collisions')
        $shapeCounts = @{}
        foreach ($collision in $collisions) {
            $shape = [string](Get-PropertyValue $collision 'ShapeName' 'Unknown')
            if (-not $shapeCounts.ContainsKey($shape)) { $shapeCounts[$shape] = 0 }
            $shapeCounts[$shape]++
        }
        $groupNames = @($simulators | ForEach-Object { [string](Get-PropertyValue $_ 'GroupName' 'Unknown') } | Sort-Object -Unique)
        $resource = Get-PropertyValue $slot 'Resource'
        $fileName = Get-PropertyValue $resource 'FileName'
        $files = if ($fileName) { @([string]$fileName) } else { @() }
        $clothing = @($simulators | Where-Object { [bool](Get-PropertyValue $_ 'IsClothing' $false) }).Count
        $groups.Add([ordered]@{
            ResourceIndex = $resourceIndex
            GroupNames = $groupNames
            SimulatorCount = $simulators.Count
            ReportedSimulatorCount = Get-PropertyValue $slot 'SimulatorCount'
            CollisionCount = $collisions.Count
            ReportedCollisionCount = Get-PropertyValue $slot 'CollisionCount'
            ClothingSimulatorCount = $clothing
            ShapeCounts = $shapeCounts
            PhybFiles = $files
            SimulatorVectorStatus = [string](Get-PropertyValue $slot 'SimulatorVectorStatus' 'Unknown')
            CollisionVectorStatus = [string](Get-PropertyValue $slot 'CollisionVectorStatus' 'Unknown')
        })
    }
    return ,$groups.ToArray()
}

function Get-StageSummary {
    param([object]$Stage, [string]$ExpectedName)
    if ($null -eq $Stage) {
        return [ordered]@{
            Stage = $ExpectedName; Captured = $false; Status = 'MissingStage'; ModuleAddress = $null
            ModuleSkeletonAddress = $null; ResourceGroups = @(); ClothingSimulatorCount = 0
            ShapeCounts = @{}; SkirtBoneCount = 0; SkirtBoneNames = @(); MissingReason = 'Stage object absent'
        }
    }
    $groups = Get-ResourceGroupSummary $Stage
    $shapeCounts = @{}
    $clothing = 0
    foreach ($group in $groups) {
        $clothing += [int](Get-PropertyValue $group 'ClothingSimulatorCount' 0)
        foreach ($entry in (Get-PropertyValue $group 'ShapeCounts' @{}).GetEnumerator()) {
            if (-not $shapeCounts.ContainsKey($entry.Key)) { $shapeCounts[$entry.Key] = 0 }
            $shapeCounts[$entry.Key] += [int]$entry.Value
        }
    }
    $bones = As-Array (Get-PropertyValue $Stage 'SkirtBones')
    $captured = [bool](Get-PropertyValue $Stage 'Captured' $false)
    $status = [string](Get-PropertyValue $Stage 'Status' 'Unknown')
    return [ordered]@{
        Stage = $ExpectedName
        Captured = $captured
        Status = $status
        ModuleAddress = Get-PropertyValue $Stage 'ModuleAddress'
        ModuleSkeletonAddress = Get-PropertyValue $Stage 'ModuleSkeletonAddress'
        FrameDeltaTime = Get-PropertyValue $Stage 'FrameDeltaTime'
        ResourceGroups = $groups
        ClothingSimulatorCount = $clothing
        ShapeCounts = $shapeCounts
        SkirtBoneCount = $bones.Count
        SkirtBoneNames = @($bones | ForEach-Object { [string](Get-PropertyValue $_ 'BoneName' '') } | Where-Object { $_ })
        UsableLocalCount = @($bones | Where-Object { Is-UsableLocalPose $_ }).Count
        UsableModelCount = @($bones | Where-Object { Is-UsableModelPose $_ }).Count
        RawLocalDirtyCount = @($bones | Where-Object { $null -ne (Get-PropertyValue $_ 'LocalInSync') -and -not (Is-TrueOne (Get-PropertyValue $_ 'LocalInSync')) }).Count
        RawModelDirtyCount = @($bones | Where-Object { $null -ne (Get-PropertyValue $_ 'ModelInSync') -and -not (Is-TrueOne (Get-PropertyValue $_ 'ModelInSync')) }).Count
    }
}

function Get-QAngleSummary {
    param([object[]]$StageObjects)
    $pairs = @(
        @('BeforeGamePhysics', 'AfterGamePhysics'),
        @('AfterGamePhysics', 'AfterFFMMDBodyWrite'),
        @('AfterFFMMDBodyWrite', 'FinalRender')
    )
    $maps = @{}
    foreach ($stage in $StageObjects) { $maps[[string](Get-PropertyValue $stage 'Stage' '')] = Get-BoneMap $stage }
    $local = [System.Collections.Generic.List[object]]::new()
    $model = [System.Collections.Generic.List[object]]::new()
    $comparisons = [System.Collections.Generic.List[object]]::new()
    foreach ($pair in $pairs) {
        $pairLocal = [System.Collections.Generic.List[object]]::new()
        $pairModel = [System.Collections.Generic.List[object]]::new()
        $left = if ($maps.ContainsKey($pair[0])) { $maps[$pair[0]] } else { @{} }
        $right = if ($maps.ContainsKey($pair[1])) { $maps[$pair[1]] } else { @{} }
        foreach ($key in ($left.Keys | Where-Object { $right.ContainsKey($_) })) {
            $a = $left[$key]; $b = $right[$key]
            $name = Get-PropertyValue $a 'BoneName'
            if ((Get-PropertyValue $a 'PoseAddress') -ne (Get-PropertyValue $b 'PoseAddress') -or
                (Get-PropertyValue $a 'HavokSkeletonAddress') -ne (Get-PropertyValue $b 'HavokSkeletonAddress')) { continue }
            if ((Is-UsableLocalPose $a) -and (Is-UsableLocalPose $b)) {
                $angle = Quaternion-AngleDegrees (Get-PropertyValue $a 'RawLocalRotation') (Get-PropertyValue $b 'RawLocalRotation')
                if ($null -ne $angle) { $pairLocal.Add([pscustomobject]@{ BoneName = $name; From = $pair[0]; To = $pair[1]; AngleDegrees = $angle }) }
            }
            if ((Is-UsableModelPose $a) -and (Is-UsableModelPose $b)) {
                $angle = Quaternion-AngleDegrees (Get-PropertyValue $a 'RawModelRotation') (Get-PropertyValue $b 'RawModelRotation')
                if ($null -ne $angle) { $pairModel.Add([pscustomobject]@{ BoneName = $name; From = $pair[0]; To = $pair[1]; AngleDegrees = $angle }) }
            }
        }
        $local.AddRange($pairLocal.ToArray()); $model.AddRange($pairModel.ToArray())
        $comparisons.Add([ordered]@{
            From = $pair[0]; To = $pair[1]
            LocalComparedCount = $pairLocal.Count; LocalMax = Get-MaxAngle $pairLocal.ToArray()
            ModelComparedCount = $pairModel.Count; ModelMax = Get-MaxAngle $pairModel.ToArray()
            UnavailableMeaning = 'A zero ComparedCount with null Max means no eligible synchronized raw-cache pair; it is not zero physical motion.'
        })
    }
    $localMax = Get-MaxAngle $local.ToArray()
    $modelMax = Get-MaxAngle $model.ToArray()
    return [ordered]@{
        Local = [ordered]@{ Max = $localMax; ComparedCount = $local.Count; Meaning = 'Only LocalInSync=1 raw locals are compared; sign-equivalent quaternions are folded.' }
        Model = [ordered]@{ Max = $modelMax; ComparedCount = $model.Count; Meaning = 'Only ModelInSync=1 raw models are compared; these are cache observations, not simulator ownership.' }
        Comparisons = $comparisons.ToArray()
        CacheLimit = 'RawLocalDirty (LocalInSync != 1) and RawModelDirty (ModelInSync != 1) samples are excluded. qangle is a bounded cache-to-cache observation, not authoritative final pose or skirt control evidence.'
    }
}

function Get-FrameSummary {
    param([string]$JsonPath)
    $document = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
    $schema = [int](Get-PropertyValue $document 'SchemaVersion' 0)
    $physics = Get-PropertyValue $document 'SkirtPhysics'
    $expectedStages = @('BeforeGamePhysics', 'AfterGamePhysics', 'AfterFFMMDBodyWrite', 'FinalRender')
    $actualStages = As-Array (Get-PropertyValue $physics 'Stages')
    $stageByName = @{}
    foreach ($stage in $actualStages) { $stageByName[[string](Get-PropertyValue $stage 'Stage' '')] = $stage }
    $namesToSummarize = @($expectedStages) + @($stageByName.Keys | Where-Object { $_ -and $_ -notin $expectedStages } | Sort-Object)
    $stageSummary = foreach ($name in $namesToSummarize) {
        $stageObject = if ($stageByName.ContainsKey($name)) { $stageByName[$name] } else { $null }
        Get-StageSummary $stageObject $name
    }
    $allSkirtNames = @($stageSummary | ForEach-Object { $_.SkirtBoneNames } | Sort-Object -Unique)
    $missingStages = @($stageSummary | Where-Object { -not $_.Captured -or $_.Status -eq 'MissingStage' } | ForEach-Object { $_.Stage })
    $partialFields = @('BeforePartials', 'AfterPartials', 'FinalPartials')
    $missingPartials = @($partialFields | Where-Object { (As-Array (Get-PropertyValue $document $_)).Count -eq 0 })
    $legacyNote = [string](Get-PropertyValue $document 'PhysicsObservationLegacyOverlayNote' '')
    $observationNote = [string](Get-PropertyValue $document 'PhysicsObservationNote' '')
    $cal = Get-PropertyValue $document 'Calibration'
    $legacyEnabled = [bool](Get-PropertyValue $document 'PhysicsObservationLegacyOverlayEnabled' $false) -or
        [bool](Get-PropertyValue $cal 'SkirtSimEnabled' $false)
    $statusWarnings = [System.Collections.Generic.List[string]]::new()
    if ($schema -lt 6) { $statusWarnings.Add("Unsupported schema $schema; expected 6 or a future version.") }
    if ($schema -gt 7) { $statusWarnings.Add("Future schema $schema parsed by tolerant field lookup.") }
    if ($missingStages.Count -gt 0) { $statusWarnings.Add("Missing or uncaptured stages: $($missingStages -join ', ').") }
    if ($missingPartials.Count -gt 0) { $statusWarnings.Add("Missing partial snapshots: $($missingPartials -join ', ').") }
    if ($allSkirtNames.Count -eq 0) { $statusWarnings.Add('No skirt bones were observed in any native stage.') }
    if ($legacyEnabled) { $statusWarnings.Add('Legacy skirt overlay contamination is possible; native attribution is limited.') }
    if ($observationNote) { $statusWarnings.Add($observationNote) }
    $qangles = Get-QAngleSummary $actualStages
    return [ordered]@{
        File = [io.path]::GetFileName($JsonPath)
        FullPath = [io.path]::GetFullPath($JsonPath)
        SchemaVersion = $schema
        CapturedUtc = Get-PropertyValue $document 'CapturedUtc'
        TargetName = Get-PropertyValue $document 'TargetName'
        MotionPath = Get-PropertyValue $document 'MotionPath'
        Frame = Get-PropertyValue $document 'Frame'
        NativeStatus = Get-PropertyValue $physics 'Status' 'MissingSkirtPhysics'
        ModuleId = Get-PropertyValue $physics 'ClientStructsModuleId'
        SkeletonAddress = Get-PropertyValue $physics 'SkeletonAddress'
        StageStatuses = $stageSummary
        MissingStages = $missingStages
        ResourceGroupCount = ($stageSummary | ForEach-Object { @($_.ResourceGroups).Count } | Measure-Object -Maximum).Maximum
        ClothingSimulatorCounts = @($stageSummary | ForEach-Object { [ordered]@{ Stage = $_.Stage; Count = $_.ClothingSimulatorCount } })
        ShapeCounts = @($stageSummary | ForEach-Object { [ordered]@{ Stage = $_.Stage; Counts = $_.ShapeCounts } })
        SkirtBoneNames = $allSkirtNames
        SkirtBoneCount = $allSkirtNames.Count
        MissingPartials = $missingPartials
        LegacyContamination = [ordered]@{ Suspected = $legacyEnabled; EnabledField = [bool](Get-PropertyValue $document 'PhysicsObservationLegacyOverlayEnabled' $false); CalibrationEnabled = [bool](Get-PropertyValue $cal 'SkirtSimEnabled' $false); Note = $legacyNote }
        QuaternionAngles = $qangles
        Warnings = $statusWarnings.ToArray()
    }
}

if (Test-Path -LiteralPath $Path -PathType Container) {
    $jsonFiles = @(Get-ChildItem -LiteralPath $Path -Filter '*.json' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
} elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
    $jsonFiles = @((Get-Item -LiteralPath $Path).FullName)
} else {
    throw "Diagnostics path does not exist: $Path"
}
if ($jsonFiles.Count -eq 0) { throw "No JSON diagnostics found at: $Path" }
if ([io.path]::GetFullPath($OutputPath) -in $jsonFiles) { throw 'OutputPath points to an input diagnostic. Original JSON files may not be overwritten.' }

$frames = [System.Collections.Generic.List[object]]::new()
$errors = [System.Collections.Generic.List[object]]::new()
foreach ($jsonFile in $jsonFiles) {
    try { $frames.Add((Get-FrameSummary $jsonFile)) }
    catch { $errors.Add([ordered]@{ File = [io.path]::GetFileName($jsonFile); Error = $_.Exception.Message; Line = $_.InvocationInfo.ScriptLineNumber }) }
}

$summary = [ordered]@{
    GeneratedUtc = [datetime]::UtcNow.ToString('O')
    InputPath = [io.path]::GetFullPath($Path)
    ReadOnlyInput = $true
    SupportedSchema = '6 and future schemas with tolerant field lookup'
    FileCount = $frames.Count
    ErrorCount = $errors.Count
    Errors = $errors.ToArray()
    Frames = $frames.ToArray()
    Interpretation = [ordered]@{
        QAngle = 'Quaternion sign double-cover is folded with abs(dot). Angles only compare matching bone names and synchronized raw caches.'
        RawCache = 'Raw local/model arrays are bounded observations. Dirty caches are excluded from pose comparisons; no simulator ownership is inferred.'
        ResourceGroups = 'Resource index groups simulator/collision vectors and .phyb metadata; it does not prove which simulator controls a skirt bone.'
    }
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) { New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null }
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Wrote read-only diagnostics summary: $([io.path]::GetFullPath($OutputPath)) ($($frames.Count) frame(s), $($errors.Count) error(s))"
