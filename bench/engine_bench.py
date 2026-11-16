#!/usr/bin/env python3
"""Benchmark one llama-server configuration: load time, speed, prompt reuse and parallel throughput.

Run it once per configuration (another llama.cpp build, another flag) and compare the JSON receipts.
Standard library only. It never touches the tray's engine: it starts its own llama-server on a spare port
and stops it afterwards, or measures an endpoint that is already running.

Scenarios (--scenarios, comma-separated):
  load        seconds from process start until /health answers, and GPU memory in use (needs --server)
  decode      a fixed coding task, 512 tokens, thinking off and on: tokens per second, draft acceptance
  prefill     one long unique prompt: prompt tokens per second
  split       the same instructions in the system message, a different chunk in each user message
  single      instructions and chunk in one user message
  think       one chunk with thinking on, retried with thinking off
  interleave  another client's request between chunks
  parallel    the same chunks one at a time, then --parallel at a time: total tokens per second

Why split and single differ: Qwen3.8 mixes attention with recurrent layers, so llama-server cannot roll its
state back to an arbitrary token. It returns to a context checkpoint, and it makes one at the start of the
last user message. Instructions that sit in an earlier message than the changing part are reused; in the
same message they are processed again on every call.

Start a private llama-server for the run. Everything after "--" goes to llama-server unchanged, so pass
the flags the tray would pass:
  python engine_bench.py --server llama.cpp/llama-server.exe --model <gguf> --json b12360.json \
      -- -ngl 999 --fit off --load-mode none -c 131072 -np 2 --kv-unified ...

Measure a running endpoint (key from the LOCAL_QWEN_API_KEY environment variable):
  python engine_bench.py --url http://127.0.0.1:8000/v1 --model-name qwen3.8-27b-uncensored-q5_k_m --scenarios split,single
"""
import argparse, json, os, subprocess, tempfile, time, urllib.request
from concurrent.futures import ThreadPoolExecutor

WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar "
         "papa quebec romeo sierra tango uniform victor whiskey xray yankee zulu").split()
CODING_TASK = ("Write a complete Python implementation of a bounded LRU cache with get, put, eviction, type hints, "
               "and unittest tests. Explain its time complexity.")


def filler(seed, n_words):
    # Deterministic text; two seeds differ from the first word, so nothing after a shared prefix matches.
    out, x = [], seed * 7919 + 13
    for _ in range(n_words):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(WORDS[x % len(WORDS)])
    return " ".join(out)


def gpu_memory_used():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits", "--id=0"],
                             capture_output=True, text=True, timeout=15)
        return int(out.stdout.strip()) if out.returncode == 0 else None
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return None


class Bench:
    def __init__(self, a, key):
        self.a, self.url, self.key, self.rows = a, a.url.rstrip("/"), key, []
        self.instructions = ("You extract people and facts from the text the user sends. Follow every rule below.\n"
                             + "\n".join(f"Rule {i}: {filler(1000 + i, 40)}." for i in range(max(1, a.prefix_words // 40))))

    def chunk(self, i): return f"Chunk {i}: " + filler(i, self.a.chunk_words)
    def split(self, i): return [{"role": "system", "content": self.instructions}, {"role": "user", "content": self.chunk(i)}]
    def single(self, i): return [{"role": "user", "content": self.instructions + "\n\n" + self.chunk(i)}]
    def other(self, i):
        return [{"role": "system", "content": "You are a coding agent. " + filler(5000, 300)},
                {"role": "user", "content": f"Task {i}: " + filler(6000 + i, 200)}]

    def record(self, row):
        self.rows.append(row)
        print("  ".join(f"{k} {v}" for k, v in row.items()), flush=True)
        return row

    def call(self, scenario, label, messages, thinking=False, max_tokens=None):
        body = {"model": self.a.model_name, "messages": messages, "max_tokens": max_tokens or self.a.max_tokens, "temperature": 0,
                "seed": 42, "stream": False, "chat_template_kwargs": {"enable_thinking": thinking}}
        request = urllib.request.Request(self.url + "/chat/completions", json.dumps(body).encode(),
                                         {"Content-Type": "application/json", "Authorization": "Bearer " + self.key})
        started = time.perf_counter()
        with urllib.request.urlopen(request, timeout=3600) as response:
            t = json.load(response).get("timings", {})
        drafted = t.get("draft_n") or 0
        return self.record({"scenario": scenario, "request": label, "processed": t.get("prompt_n"), "reused": t.get("cache_n"),
                            "prompt_ms": round(t.get("prompt_ms", 0)), "prompt_tok_s": round(t.get("prompt_per_second", 0), 1),
                            "generated": t.get("predicted_n"), "tok_s": round(t.get("predicted_per_second", 0), 1),
                            "draft_accept": round(t.get("draft_n_accepted", 0) / drafted, 3) if drafted else None,
                            "wall_s": round(time.perf_counter() - started, 2)})

    def run(self, scenarios):
        if "decode" in scenarios:
            for thinking in (False, True):
                for n in (1, 2, 3):
                    # A new seed-numbered line per run keeps each prompt out of the cache of the one before.
                    self.call("decode", f"thinking {'on' if thinking else 'off'}, run {n}",
                              [{"role": "user", "content": f"Reference fixture {n}{int(thinking)}.\n{CODING_TASK}"}], thinking, 512)
        if "prefill" in scenarios:
            self.call("prefill", f"{self.a.prefill_words} words", [{"role": "user", "content": filler(77, self.a.prefill_words) + "\nReply OK."}], max_tokens=1)
        if "split" in scenarios:
            for i in (1, 2, 3, 4): self.call("split", f"chunk {i}", self.split(i))
        if "single" in scenarios:
            for i in (11, 12, 13): self.call("single", f"chunk {i}", self.single(i))
        if "think" in scenarios:
            self.call("think", "chunk 21, thinking on", self.split(21), thinking=True)
            self.call("think", "chunk 21, retry thinking off", self.split(21))
        if "interleave" in scenarios:
            for i in (31, 32, 33):
                self.call("interleave", f"chunk {i}", self.split(i))
                self.call("interleave", f"other client {i}", self.other(i))
        if "parallel" in scenarios:
            self.call("parallel", "warm-up", self.split(40))
            for width in (1, self.a.parallel):
                first = 41 if width == 1 else 61
                started = time.perf_counter()
                with ThreadPoolExecutor(width) as pool:
                    rows = list(pool.map(lambda i: self.call("parallel", f"chunk {i}, {width} at a time", self.split(i), max_tokens=self.a.parallel_tokens),
                                         range(first, first + self.a.parallel_chunks)))
                wall, tokens = time.perf_counter() - started, sum(r["generated"] or 0 for r in rows)
                self.record({"scenario": "parallel", "request": f"total, {width} at a time", "generated": tokens, "wall_s": round(wall, 2), "tok_s": round(tokens / wall, 1)})


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--url", help="OpenAI-compatible base URL of a running endpoint, e.g. http://127.0.0.1:8000/v1")
    p.add_argument("--server", help="llama-server executable to start for this run, instead of --url")
    p.add_argument("--model", help="GGUF file for --server")
    p.add_argument("--port", type=int, default=18090, help="port for --server")
    p.add_argument("--model-name", default="engine-bench", help="model field sent with each request")
    p.add_argument("--scenarios", default="load,decode,prefill,split,single,think")
    p.add_argument("--prefix-words", type=int, default=2400, help="size of the shared instructions (about 1.7 tokens per word)")
    p.add_argument("--chunk-words", type=int, default=500)
    p.add_argument("--prefill-words", type=int, default=9000)
    p.add_argument("--max-tokens", type=int, default=8, help="reply length in the prompt-reuse scenarios")
    p.add_argument("--parallel", type=int, default=2, help="requests at a time in the parallel scenario")
    p.add_argument("--parallel-chunks", type=int, default=4)
    p.add_argument("--parallel-tokens", type=int, default=300)
    p.add_argument("--json", help="write every row to this file")
    p.add_argument("server_flags", nargs=argparse.REMAINDER, help='llama-server flags for --server, after "--"')
    a = p.parse_args()
    if bool(a.url) == bool(a.server): p.error("give either --url or --server")
    scenarios = a.scenarios.split(",")
    key = os.environ.get("LOCAL_QWEN_API_KEY", "engine-bench")
    process = log = None
    load_row = None
    if a.server:
        if not a.model: p.error("--server needs --model")
        log = tempfile.NamedTemporaryFile("w+", suffix=".log", delete=False)
        command = [a.server, "-m", a.model, "--host", "127.0.0.1", "--port", str(a.port), "--jinja", "--log-colors", "off", "-lv", "4"] + [f for f in a.server_flags if f != "--"]
        print("starting:", " ".join(command), flush=True)
        memory_before = gpu_memory_used()
        started = time.perf_counter()
        # The key travels in the environment, as in the tray, so it never shows up in a process listing.
        process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env=dict(os.environ, LLAMA_API_KEY=key))
        a.url = f"http://127.0.0.1:{a.port}/v1"
    try:
        if process:
            while True:
                if process.poll() is not None: raise SystemExit(f"llama-server exited with {process.returncode}; see {log.name}")
                if time.perf_counter() - started > 600: raise SystemExit(f"llama-server did not become ready; see {log.name}")
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{a.port}/health", timeout=2) as response:
                        if response.status == 200: break
                except OSError: time.sleep(0.1)
            memory = gpu_memory_used()
            load_row = {"scenario": "load", "request": "start to /health", "wall_s": round(time.perf_counter() - started, 2),
                        "gpu_mib_before": memory_before, "gpu_mib_loaded": memory}
        bench = Bench(a, key)
        if load_row and "load" in scenarios: bench.record(load_row)
        bench.run(scenarios)
        if a.json:
            with open(a.json, "w", encoding="utf-8") as out: json.dump({"command": a.server and command, "url": a.url, "rows": bench.rows}, out, indent=2)
    finally:
        if process:
            process.terminate()
            try: process.wait(30)
            except subprocess.TimeoutExpired: process.kill()
            print("server log:", log.name)


if __name__ == "__main__":
    main()
