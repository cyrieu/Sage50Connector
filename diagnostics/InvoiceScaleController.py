#!/usr/bin/env python3
"""Own-lab overnight controller. Protected credentials live outside the repo.
Start only after an isolated DB/backend, approved diagnostic EXE and seed smoke.
"""
import argparse, csv, datetime, json, os, pathlib, signal, subprocess, time, uuid
ROOT = pathlib.Path(__file__).resolve().parents[1]
DEST = 'C:/src/InvoiceWindowOvernightV3'
PARSER = argparse.ArgumentParser()
PARSER.add_argument('--state-directory', required=True)
PARSER.add_argument('--ssh-host', required=True)
PARSER.add_argument('--ssh-user', required=True)
PARSER.add_argument('--ssh-key', required=True)
args = PARSER.parse_args()
OUT = pathlib.Path(args.state_directory)
CONFIG = json.loads((OUT / 'config.json').read_text())
DB = 'sage50_invoice_scale_20261007'
SSH = ['ssh', '-i', args.ssh_key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'ServerAliveInterval=30', '-o', 'ServerAliveCountMax=3', args.ssh_user + '@' + args.ssh_host]
SCP = ['scp', '-q', '-i', args.ssh_key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes']
REMOTE = args.ssh_user + '@' + args.ssh_host + ':'
PSQL = ['/opt/homebrew/opt/libpq/bin/psql', '-h', '127.0.0.1', '-d', DB, '-v', 'ON_ERROR_STOP=1', '-At']

def sql(query):
    p = subprocess.run(PSQL, input=query, capture_output=True, text=True, check=True)
    return p.stdout.strip()

def remote(script, timeout=60):
    p = subprocess.run(SSH + ['powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ' + script], capture_output=True, text=True, timeout=timeout)
    if p.returncode: raise RuntimeError('Remote command failed; exit=' + str(p.returncode))
    return p.stdout.strip()

def fetch(path):
    # Remote paths come only from our own Windows pipeline artifacts.
    return json.loads(remote('"Get-Content -LiteralPath \'' + path + '\' -Raw"'))

def put_json(value, remote_path, name):
    local = OUT / name
    local.write_text(json.dumps(value, indent=2)); local.chmod(0o600)
    subprocess.run(SCP + [str(local), REMOTE + remote_path], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

def status(phase, **extra):
    value = dict(phase=phase, updatedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), **extra)
    p = OUT / 'controller-state.json'; tmp=p.with_suffix('.tmp')
    tmp.write_text(json.dumps(value, indent=2)); tmp.replace(p)

def enqueue(parameters):
    job = str(uuid.uuid4())
    # Tokens are never part of SQL output or command-line arguments.
    sql("INSERT INTO desktop_platform_jobs (id,item_id,status,platform_entity,type,priority,parameters) VALUES ('" + job + "','" + CONFIG['ConnectionId'] + "','enqueued','INVOICES','LIST_FETCH',40,'" + json.dumps(parameters) + "');")
    return job

def driver(label):
    script = DEST + '/diagnostics/Run-InvoiceIngestE2E.ps1'
    command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File ' + script + ' -Destination ' + DEST + ' -ConfigPath ' + DEST + '/e2e-secrets/invoice-ingest-e2e.json -TaskName Sage50InvoiceScaleE2E -TimeoutMinutes 240'
    sample_path=DEST+'/results/e2e-memory-'+label+'.csv'
    sample_command='powershell.exe -NoProfile -ExecutionPolicy Bypass -File '+DEST+'/diagnostics/Sample-ProcessMemory.ps1 -ExecutablePath '+DEST+'/bin/Release/Sage50Connector.exe -CsvPath '+sample_path+' -StartTimeoutSeconds 120'
    sampler=subprocess.Popen(SSH+[sample_command],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    try:
        p = subprocess.run(SSH + [command], capture_output=True, text=True, timeout=15000)
        if p.returncode: raise RuntimeError('Invoice E2E driver failed at ' + label)
        # Driver output is allowlisted result JSON, no raw payload/auth.
        result = json.loads(p.stdout); (OUT / (label + '-driver.json')).write_text(json.dumps(result, indent=2))
        if result['status'] != 'completed': raise RuntimeError('Invoice E2E driver did not complete')
    finally:
        try: sampler.wait(timeout=150)
        except subprocess.TimeoutExpired: sampler.terminate()
    if sampler.returncode != 0: raise RuntimeError('E2E external memory sampler failed')
    local_csv=OUT/('e2e-memory-'+label+'.csv')
    subprocess.run(SCP+[REMOTE+sample_path,str(local_csv)],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    with local_csv.open() as handle: samples=list(csv.DictReader(handle))
    if not samples: raise RuntimeError('E2E memory sampler captured no samples')
    memory={'samples':len(samples),'peakPrivateBytes':max(int(row['private_bytes']) for row in samples),'peakWorkingSetBytes':max(int(row['working_set_bytes']) for row in samples),'elapsedMs':int(samples[-1]['elapsed_ms'])}
    (OUT/(label+'-memory.json')).write_text(json.dumps(memory,indent=2))
    return memory

def db_summary(job, target):
    item = CONFIG['ConnectionId']
    row = sql("SELECT json_build_object('jobStatus',(SELECT status FROM desktop_platform_jobs WHERE id='"+job+"'),'platformRows',count(*),'distinctIds',count(DISTINCT platform_id),'pendingNormalization',count(*) FILTER (WHERE _needs_normalization OR _normalized_at IS NULL),'lines',sum(jsonb_array_length(platform_data::jsonb->'lines')),'normalizedRows',(SELECT count(*) FROM n_invoices WHERE item_id='"+item+"'),'refreshStatus',(SELECT status FROM refresh_entity_runs WHERE item_id='"+item+"' AND entity='INVOICES' ORDER BY started_at DESC LIMIT 1),'newRows',(SELECT new_rows_count FROM refresh_entity_runs WHERE item_id='"+item+"' AND entity='INVOICES' ORDER BY started_at DESC LIMIT 1),'updatedRows',(SELECT updated_rows_count FROM refresh_entity_runs WHERE item_id='"+item+"' AND entity='INVOICES' ORDER BY started_at DESC LIMIT 1),'emptyLineIds',(SELECT count(*) FROM platform_entities pe CROSS JOIN LATERAL jsonb_array_elements(pe.platform_data::jsonb->'lines') line WHERE pe.item_id='"+item+"' AND pe.platform_entity='INVOICES' AND coalesce(line->>'id','')='')) FROM platform_entities WHERE item_id='"+item+"' AND platform_entity='INVOICES';")
    result = json.loads(row)
    expected_lines=sum(5+n%16 for n in range(1,target+2) if n!=2)
    if not (result['jobStatus']=='completed' and result['refreshStatus']=='SUCCESS' and result['platformRows']==target and result['distinctIds']==target and result['normalizedRows']==target and result['pendingNormalization']==0 and result['lines']==expected_lines and result['emptyLineIds']==0):
        raise RuntimeError('DB verification failed: ' + json.dumps(result))
    return result

def checkpoint(state):
    target=state['targetInvoices']; measurement=fetch(state['benchmark'])
    if measurement['matchedCount']!=target: raise RuntimeError('SDK benchmark fixture count differs from target')
    status('e2e-running', targetInvoices=target, benchmark=measurement)
    wire=OUT / ('wire-'+str(target)+'.json')
    progress=OUT / ('wire-'+str(target)+'-progress.json')
    proxylog=open(OUT / ('proxy-'+str(target)+'.log'),'w')
    proxy=subprocess.Popen(['/opt/homebrew/opt/node@22/bin/node', str(ROOT/'diagnostics/InvoiceIngestCaptureProxy.js'),'4007','4008',str(wire),str(progress),'0',measurement['fingerprint']],stdout=proxylog,stderr=subprocess.STDOUT)
    try:
        time.sleep(1)
        if proxy.poll() is not None: raise RuntimeError('Capture proxy failed to start')
        job=enqueue({'limit':50,'updated_at':'1900-01-01T00:00:00Z'})
        full_memory=driver('full-'+str(target))
    finally:
        if proxy.poll() is None: proxy.send_signal(signal.SIGTERM)
        proxy.wait(timeout=30); proxylog.close()
    capture=json.loads(wire.read_text())
    if not capture['fingerprintMatches'] or capture['uniqueIds']!=target or capture['missingIds'] or capture['duplicatePayloadMismatches']:
        raise RuntimeError('Wire payload parity failed: '+json.dumps(capture))
    full=db_summary(job,target)
    nonempty_incremental = None
    if target == 652:
        # Derive a cutoff in Sage's timestamp domain, then insert genuinely new
        # invoices. Existing rows are strictly older; no timezone assumption.
        cutoff=sql("SELECT to_char(max((platform_data::jsonb->>'lastSavedAt')::timestamp)+interval '1 second','YYYY-MM-DD\"T\"HH24:MI:SS.US') FROM platform_entities WHERE item_id='"+CONFIG['ConnectionId']+"' AND platform_entity='INVOICES';")
        if not cutoff: raise RuntimeError('Cannot derive incremental Sage timestamp cutoff')
        seedcommand='powershell.exe -NoProfile -ExecutionPolicy Bypass -File '+DEST+'/diagnostics/Run-InvoiceSeeding.ps1 -Destination '+DEST+' -StartNumber 654 -Count 10 -CandidateDate 2026-08-15 -TimeoutMinutes 10'
        seed=subprocess.run(SSH+[seedcommand],capture_output=True,text=True,timeout=900)
        if seed.returncode: raise RuntimeError('Nonempty incremental seed failed')
        incrementalwire=OUT/'new-ten-wire.json'
        newlog=open(OUT/'new-ten-proxy.log','w')
        newproxy=subprocess.Popen(['/opt/homebrew/opt/node@22/bin/node',str(ROOT/'diagnostics/InvoiceIngestCaptureProxy.js'),'4007','4008',str(incrementalwire)],stdout=newlog,stderr=subprocess.STDOUT)
        try:
            time.sleep(1)
            newjob=enqueue({'limit':50,'updated_at':cutoff,'include_missing_timestamps':False})
            driver('incremental-new-ten')
            nonempty_incremental=db_summary(newjob,662)
        finally:
            if newproxy.poll() is None:newproxy.send_signal(signal.SIGTERM)
            newproxy.wait(timeout=30);newlog.close()
        newcapture=json.loads(incrementalwire.read_text())
        if newcapture['uniqueIds']!=10 or newcapture['reportRows']!=10: raise RuntimeError('Expected exactly ten new incremental invoices')
        if nonempty_incremental['newRows']!=10 or nonempty_incremental['updatedRows']!=0: raise RuntimeError('Expected exactly ten new persisted incremental invoices')
        nonempty_incremental['reportedRows']=newcapture['reportRows']
    # The proxy is stopped: connect directly to backend through the same tunnel
    # by briefly serving an uninstrumented proxy for incremental verification.
    plog=open(OUT / ('incremental-proxy-'+str(target)+'.log'),'w')
    proxy=subprocess.Popen(['/opt/homebrew/opt/node@22/bin/node',str(ROOT/'diagnostics/InvoiceIngestCaptureProxy.js'),'4007','4008',str(OUT/('incremental-wire-'+str(target)+'.json'))],stdout=plog,stderr=subprocess.STDOUT)
    try:
        time.sleep(1)
        inc=enqueue({'limit':50,'updated_at':'2027-01-01T00:00:00Z'})
        driver('incremental-'+str(target))
        incremental=db_summary(inc,662 if target==652 else target)
    finally:
        if proxy.poll() is None: proxy.send_signal(signal.SIGTERM)
        proxy.wait(timeout=30);plog.close()
    emptycapture=json.loads((OUT/('incremental-wire-'+str(target)+'.json')).read_text())
    if emptycapture['reportRows']!=0 or incremental['newRows']!=0 or incremental['updatedRows']!=0:
        raise RuntimeError('Future cutoff did not produce an empty unchanged incremental fetch')
    acknowledgement={'status':'completed','targetInvoices':target,'benchmark':measurement,'full':full,'fullIngestMemory':full_memory,'incremental':incremental,'nonemptyIncremental':nonempty_incremental,'wireFingerprintMatches':True,'completedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}
    put_json(acknowledgement,state['ackPath'].replace('\\','/'),'checkpoint-'+str(target)+'.json')
    status('checkpoint-completed',**acknowledgement)

if __name__ == '__main__':
    tunnel_log=open(OUT/'tunnel.log','a')
    tunnel=subprocess.Popen(SSH[:-1]+['-N','-o','ExitOnForwardFailure=yes','-R','14007:localhost:4007',SSH[-1]],stdout=tunnel_log,stderr=subprocess.STDOUT)
    awake=subprocess.Popen(['/usr/bin/caffeinate','-i','-w',str(os.getpid())])
    try:
        status('monitoring')
        while True:
            if tunnel.poll() is not None: raise RuntimeError('Reverse SSH tunnel stopped')
            state=fetch(DEST+'/results/scale-pipeline.json')
            (OUT/'vm-state.json').write_text(json.dumps(state,indent=2))
            if state['phase']=='awaiting-e2e':
                try: checkpoint(state)
                except Exception as error:
                    put_json({'status':'failed','error':str(error).replace(CONFIG['AccessKey'],'[REDACTED]')},state['ackPath'].replace('\\','/'),'failed-checkpoint.json')
                    raise
            elif state['phase'] in ('completed','failed'):
                status(state['phase'],vmState=state);break
            else:
                seed=None
                if state['phase']=='seeding':
                    try: seed=fetch(DEST+'/results/seed-'+str(state['batchStart'])+'-'+str(state['batchCount'])+'-attempt-1.json')
                    except Exception: pass # First invoice may not have written its heartbeat yet.
                status('monitoring',vmState=state,seedProgress=seed)
            time.sleep(30)
    except Exception as error:
        # All errors we generate are allowlisted; credentials stay in config.
        status('failed',error=str(error).replace(CONFIG['AccessKey'],'[REDACTED]'))
        raise SystemExit(1)
    finally:
        tunnel.terminate();awake.terminate();tunnel_log.close()
