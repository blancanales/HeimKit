param(
    [Parameter(Mandatory)][string]$GameManaged,
    [Parameter(Mandatory)][string]$BepInExCore,
    [Parameter(Mandatory)][string]$TestOutput
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $GameManaged 'assembly_valheim.dll'
$destination = Join-Path $TestOutput 'assembly_valheim.dll'
if ([IO.Path]::GetFullPath($source) -eq [IO.Path]::GetFullPath($destination)) { throw 'Expected a separate test output directory.' }
Add-Type -Path (Join-Path $BepInExCore 'Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($GameManaged)
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($source, $reader)
$unity = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameManaged 'UnityEngine.CoreModule.dll'))
$temp = "$destination.$([guid]::NewGuid()).tmp"
try {
    # Outside Unity, identity.eulerAngles cannot call the engine. Substitute its known
    # value only for this constant in the test copy. Keep the rest of ZDO startup intact.
    $zdo = $game.MainModule.GetType('ZDO')
    $initializer = $zdo.Methods | Where-Object Name -eq '.cctor'
    $store = $initializer.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq 'c_quaternionIdentity'
    }
    $angle = $store.Previous
    $address = $angle.Previous
    $local = $address.Previous
    $identity = $local.Previous
    if ($angle.Operand.FullName -ne 'UnityEngine.Vector3 UnityEngine.Quaternion::get_eulerAngles()' -or
        $identity.Operand.FullName -ne 'UnityEngine.Quaternion UnityEngine.Quaternion::get_identity()' -or
        $address.OpCode.Name -ne 'ldloca.s' -or $address.Operand.Index -ne 0 -or $local.OpCode.Name -ne 'stloc.0') {
        throw 'Unexpected ZDO identity initializer. Review the headless fixture against this Valheim version.'
    }
    $zero = $unity.MainModule.GetType('UnityEngine.Vector3').Methods | Where-Object Name -eq 'get_zero'
    $identity.Operand = $game.MainModule.ImportReference($zero)
    foreach ($instruction in $local, $address, $angle) {
        $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
        $instruction.Operand = $null
    }
    # Reading the null local-player field also initializes its animation tags.
    # Precompute the literal Animator CRC32 IDs in this test copy only.
    $player = $game.MainModule.GetType('Player')
    $playerInit = $player.Methods | Where-Object Name -eq '.cctor'
    foreach ($call in $playerInit.Body.Instructions) {
        if ($call.OpCode.Name -ne 'call' -or $call.Operand.FullName -ne 'System.Int32 ZSyncAnimation::GetHash(System.String)') { continue }
        $literal = $call.Previous
        if ($literal.OpCode.Name -ne 'ldstr' -or $call.Next.OpCode.Name -ne 'stsfld') { throw 'Unexpected Player animation tag initializer.' }
        [uint32]$crc = [uint32]::MaxValue
        foreach ($byte in [Text.Encoding]::UTF8.GetBytes($literal.Operand)) {
            $crc = $crc -bxor $byte
            for ($bit = 0; $bit -lt 8; $bit++) {
                $crc = if ($crc -band 1) { ($crc -shr 1) -bxor 0xEDB88320u } else { $crc -shr 1 }
            }
        }
        $literal.OpCode = [Mono.Cecil.Cil.OpCodes]::Ldc_I4
        $literal.Operand = [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32](-bnot $crc)), 0)
        $call.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
        $call.Operand = $null
    }
    $game.Write($temp)
    [IO.File]::Move($temp, $destination, $true)
} finally {
    $game.Dispose()
    $unity.Dispose()
    $resolver.Dispose()
    if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
}
