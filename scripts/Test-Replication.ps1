param([switch]$KeepRunning)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $PSScriptRoot 'Compose.ps1'
$directory = Join-Path $root 'artifacts/replication-verification'
$testEnv = Join-Path $directory 'test.env'
$files = @('--env-file',$testEnv,'-p','tourbooking-replication-test','-f','compose.replication-test.yaml')
function Invoke-Mongo([string]$Node, [string]$Script) {
    $auth = 'if (!db.getSiblingDB("admin").auth("mongo_admin",process.env.MONGO_INITDB_ROOT_PASSWORD)) throw new Error("Authentication failed"); '
    if ($Node -notin @('mongodb-a','mongodb-b','mongodb-c')) { throw 'Unexpected Mongo node' }
    $result = ($auth + $Script) | docker exec -i "tourbooking-replication-test-${Node}-1" mongosh --quiet --file /dev/stdin
    if ($LASTEXITCODE -ne 0) { throw 'Mongo verification command failed' }
    return ($result -join "`n")
}
function Wait-Check([scriptblock]$Check, [string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        try { if (& $Check) { return } } catch { $lastFailure = $_.Exception.Message }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Name. $lastFailure"
}
Push-Location $root
try {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if (-not (Test-Path -LiteralPath $testEnv)) {
        $values = foreach ($key in @('MONGO_ROOT_PASSWORD','RABBITMQ_PASSWORD','RABBITMQ_COOKIE','MONGO_KEYFILE')) {
            $key + '=' + [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        }
        [IO.File]::WriteAllLines($testEnv, $values, [Text.UTF8Encoding]::new($false))
    }
    & $compose @files config --quiet
    foreach ($node in @('rabbitmq-a','rabbitmq-b','rabbitmq-c')) { & $compose @files stop $node }
    foreach ($node in @('rabbitmq-c','rabbitmq-b','rabbitmq-a')) {
        & $compose @files up -d --no-deps --wait --wait-timeout 120 $node
    }
    & $compose @files up -d --wait --wait-timeout 240 mongodb-a mongodb-b mongodb-c rabbitmq-a rabbitmq-b rabbitmq-c
    & $compose @files exec -T mongodb-a mongosh --quiet /replica-init.js
    Wait-Check { (Invoke-Mongo mongodb-a 'print(rs.status().members.filter(m => m.state === 1 || m.state === 2).length)') -eq '3' } 'Mongo three healthy members'
    $primary = (Invoke-Mongo mongodb-a 'print(db.hello().primary.split(":")[0])').Trim()
    if ($primary -notin @('mongodb-a','mongodb-b','mongodb-c')) { throw 'Unexpected Mongo primary' }
    $marker = [Guid]::NewGuid().ToString('N')
    Invoke-Mongo $primary ('db.getSiblingDB("VerificationDB").markers.insertOne({_id:"' + $marker + '"},{writeConcern:{w:"majority",wtimeout:10000}}); print("written")') | Out-Null
    $observer = if ($primary -eq 'mongodb-a') { 'mongodb-b' } else { 'mongodb-a' }
    & $compose @files stop $primary
    try {
        Wait-Check {
            $candidate = (Invoke-Mongo $observer 'print(db.hello().primary || "")').Trim()
            $candidate -match '^mongodb-[abc]:27017$' -and $candidate -ne "${primary}:27017"
        } 'Mongo primary election'
        $elected = (Invoke-Mongo $observer 'print(db.hello().primary.split(":")[0])').Trim()
        if ($elected -eq $primary) { throw 'Primary did not change' }
        $found = Invoke-Mongo $elected ('print(db.getSiblingDB("VerificationDB").markers.countDocuments({_id:"' + $marker + '"}))')
        if ($found.Trim() -ne '1') { throw 'Majority-committed marker missing after election' }
    } finally { & $compose @files start $primary }
    docker cp (Join-Path $root 'infrastructure/mongo/verify-restore.sh') "tourbooking-replication-test-${observer}-1:/tmp/verify-restore.sh"
    if ($LASTEXITCODE -ne 0) { throw 'Mongo restore script copy failed' }
    docker exec -e "RESTORE_DATABASE=RestoreVerificationDB_$marker" "tourbooking-replication-test-${observer}-1" bash /tmp/verify-restore.sh |
        Tee-Object -FilePath (Join-Path $directory 'mongo-restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Mongo backup/restore failed' }
    $current = (Invoke-Mongo $observer 'print(db.hello().primary.split(":")[0])').Trim()
    $restored = Invoke-Mongo $current ('print(db.getSiblingDB("RestoreVerificationDB_' + $marker + '").markers.countDocuments({_id:"' + $marker + '"}))')
    if ($restored.Trim() -ne '1') { throw 'Restored Mongo marker missing' }
    $cluster = & $compose @files exec -T rabbitmq-b rabbitmqctl cluster_status --formatter json
    $state = ($cluster -join "`n") | ConvertFrom-Json
    if ($state.running_nodes.Count -ne 3) { throw 'RabbitMQ cluster must have three running nodes' }
    $cluster | Set-Content -Encoding utf8 (Join-Path $directory 'rabbit-cluster.json')
    $password = (Get-Content $testEnv | Where-Object { $_ -match '^RABBITMQ_PASSWORD=' }).Substring('RABBITMQ_PASSWORD='.Length)
    $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("tourbooking:$password"))
    $headers = @{ Authorization = "Basic $basic" }
    $queueUri = 'http://127.0.0.1:15681/api/queues/%2F/verification.quorum'
    Invoke-RestMethod $queueUri -Method Put -Headers $headers -ContentType 'application/json' -Body '{"durable":true,"arguments":{"x-queue-type":"quorum"}}' | Out-Null
    Invoke-RestMethod "$queueUri/contents" -Method Delete -Headers $headers | Out-Null
    $publish = @{ properties = @{ delivery_mode = 2 }; routing_key = 'verification.quorum'; payload = $marker; payload_encoding = 'string' } | ConvertTo-Json
    $published = Invoke-RestMethod 'http://127.0.0.1:15681/api/exchanges/%2F/amq.default/publish' -Method Post -Headers $headers -ContentType 'application/json' -Body $publish
    if (-not $published.routed) { throw 'Message was not routed' }
    & $compose @files stop rabbitmq-a
    try {
        Wait-Check {
            $queue = Invoke-RestMethod 'http://127.0.0.1:15682/api/queues/%2F/verification.quorum' -Headers $headers -TimeoutSec 10
            $queue.type -eq 'quorum' -and $queue.messages -eq 1
        } 'Quorum queue retained message after broker loss'
        $delivery = @(Invoke-RestMethod 'http://127.0.0.1:15682/api/queues/%2F/verification.quorum/get' -Method Post -Headers $headers -ContentType 'application/json' -Body '{"count":1,"ackmode":"ack_requeue_false","encoding":"auto"}')
        if ($delivery.Count -ne 1 -or $delivery[0].payload -ne $marker) { throw 'Quorum message payload mismatch' }
    } finally { & $compose @files start rabbitmq-a }
    @{ timestamp_utc = [DateTime]::UtcNow.ToString('o'); mongo_primary_before = $primary; mongo_primary_after = $elected;
       mongo_majority_write_preserved = $true; mongo_restore_verified = $true; rabbit_three_nodes = $true; quorum_message_preserved = $true;
       limitation = 'Single Docker host; no host-level HA or TLS claim' } |
        ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $directory 'result.json')
    Write-Host 'PASS Mongo election and RabbitMQ quorum broker-loss verification'
} finally {
    if (-not $KeepRunning -and (Test-Path -LiteralPath $testEnv)) {
        # Preserve a known clean last-shutdown node instead of stopping all brokers concurrently.
        foreach ($node in @('rabbitmq-a','rabbitmq-b','rabbitmq-c')) { & $compose @files stop $node }
        & $compose @files down
    }
    Pop-Location
}
