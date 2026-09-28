#!/usr/bin/env python3
"""Sanitize captures to a separate output. Inspect output before committing any fixture."""
import argparse, hashlib, json, re
p=argparse.ArgumentParser();p.add_argument('input');p.add_argument('output');a=p.parse_args()
enums={'__typename','type','product_type','feed_type','kind'}
id_keys={'id','pk','post_id','ent_id','media_id','userID','user_id','owner_id','targetID'}
identities={}
def rewrite(value,key=''):
    if isinstance(value,dict): return {k:rewrite(v,k) for k,v in value.items()}
    if isinstance(value,list): return [rewrite(v,key) for v in value]
    if isinstance(value,(str,int)) and not isinstance(value,bool) and (isinstance(value,str) or key in id_keys):
        value=str(value)
        if key in enums and re.fullmatch('[A-Za-z0-9_]+',value):return value
        if value not in identities:identities[value]=str(len(identities)+1000)
        n=identities[value]
        if value.startswith(('http://','https://')):return 'https://example.test/synthetic-'+n+'.jpg'
        if key in id_keys:return n
        return 'synthetic_'+n
    return value
with open(a.input) as f:raw=f.read()
try: values=[json.loads(raw)]
except json.JSONDecodeError: values=[json.loads(line.removeprefix('for (;;);')) for line in raw.splitlines() if line.strip()]
with open(a.output,'w') as f:
    for value in values:f.write(json.dumps(rewrite(value),ensure_ascii=False)+'\n')
