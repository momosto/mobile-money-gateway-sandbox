"""Same test as double-charge.js, for machines without k6 or Docker.

    pip install httpx
    python load/double_charge.py [--gateway http://localhost:5080] [--provider http://localhost:5090]

Sends 1,000 payment requests for 100 orders, 100 at a time, waits for callbacks and the
reconciliation worker, then asks the provider how many orders it charged more than once.
"""

import argparse
import asyncio
import collections
import json
import time
import uuid

import httpx


async def main(gateway: str, provider: str, requests: int, orders: int, concurrency: int, settle_seconds: int) -> int:
    run = uuid.uuid4().hex[:6]
    headers = {"X-Api-Key": "demo-merchant-key"}
    body = {"msisdn": "263771234567", "amountCents": 2500, "currency": "USD"}
    statuses: collections.Counter = collections.Counter()
    gate = asyncio.Semaphore(concurrency)

    async with httpx.AsyncClient(timeout=30) as client:
        await client.post(f"{provider}/v1/reset")

        async def pay(i: int) -> None:
            async with gate:
                r = await client.post(f"{gateway}/payments", json=body, headers={**headers, "Idempotency-Key": f"{run}-order-{i % orders}"})
                statuses[r.status_code] += 1

        started = time.perf_counter()
        await asyncio.gather(*(pay(i) for i in range(requests)))
        elapsed = time.perf_counter() - started

        print(f"{requests} requests for {orders} orders in {elapsed:.1f}s ({requests / elapsed:.0f} req/s); HTTP status counts: {dict(statuses)}")
        print(f"waiting {settle_seconds}s for callbacks and reconciliation...")
        await asyncio.sleep(settle_seconds)

        stats = (await client.get(f"{provider}/v1/stats")).json()
        payments = (await client.get(f"{gateway}/payments", params={"limit": 500}, headers=headers)).json()
        mine = [p for p in payments if p["idempotencyKey"].startswith(run)]
        outcome = collections.Counter(p["status"] for p in mine)

    print(f"gateway payments for this run: {len(mine)}; by status: {dict(outcome)}")
    print(f"provider ledger: {json.dumps(stats)}")
    ok = stats["referencesChargedMoreThanOnce"] == 0 and stats["charges"] <= orders and len(mine) == orders
    print("PASS: no order was charged twice" if ok else "FAIL: an order was charged twice or a payment is missing")
    return 0 if ok else 1


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--gateway", default="http://localhost:5080")
    p.add_argument("--provider", default="http://localhost:5090")
    p.add_argument("--requests", type=int, default=1000)
    p.add_argument("--orders", type=int, default=100)
    p.add_argument("--concurrency", type=int, default=100)
    p.add_argument("--settle-seconds", type=int, default=25)
    a = p.parse_args()
    raise SystemExit(asyncio.run(main(a.gateway, a.provider, a.requests, a.orders, a.concurrency, a.settle_seconds)))
