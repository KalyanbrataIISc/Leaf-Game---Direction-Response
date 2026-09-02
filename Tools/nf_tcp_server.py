#!/usr/bin/env python3
"""Serve SSVEP neurofeedback over TCP and receive Unity triggers over UDP.

NF wire format: each sample is one 24-byte record containing three
little-endian doubles, back to back with no header or framing bytes:

    [SMI_19gt23, SMI_23gt19, sampleCount]

The Unity tablet connects to TCP port 5006 for NF and sends ASCII integer
trigger datagrams to UDP port 5007 on this same computer. Every received
trigger is printed immediately with a timestamp and sender address.
"""
import argparse
import errno
import random
import select
import socket
import struct
import sys
import time
from datetime import datetime

SAMPLE_RATE_HZ = 128.0
SAMPLES_PER_WRITE = 13
WRITE_INTERVAL_SEC = SAMPLES_PER_WRITE / SAMPLE_RATE_HZ

AR_COEFF = 0.95
NOISE_STD = 0.12

RECORD_FORMAT = '<3d'
RECORD_BYTES = struct.calcsize(RECORD_FORMAT)
DEFAULT_PORT = 5006
DEFAULT_TRIGGER_PORT = 5007


def next_value(value):
    value = AR_COEFF * value + random.gauss(0, NOISE_STD)
    return max(-1.0, min(1.0, value))


class SimSource(object):
    """Fake NF, one bounded random walk per SMI column."""

    interval = WRITE_INTERVAL_SEC

    def __init__(self, seed=None):
        if seed is not None:
            random.seed(seed)
        self.smi1 = 0.0
        self.smi2 = 0.0
        self.sample_count = 0

    def describe(self):
        return 'simulated NF (AR(1) walk, %.1f ms cadence)' % (self.interval * 1000)

    def sample(self):
        self.smi1 = next_value(self.smi1)
        self.smi2 = next_value(self.smi2)
        self.sample_count += 1
        return (self.smi1, self.smi2, float(self.sample_count))


class FileSource(object):
    """Real NF, mirrored from the nf.txt acquisition keeps rewriting."""

    def __init__(self, path, interval):
        self.path = path
        self.interval = interval
        self.warned_missing = False

    def describe(self):
        return 'nf.txt at %s (polled every %.0f ms)' % (self.path, self.interval * 1000)

    def sample(self):
        try:
            with open(self.path, 'rb') as source_file:
                raw = source_file.read(RECORD_BYTES)
        except (IOError, OSError) as exc:
            if not self.warned_missing:
                print('nf source unreadable (%s) - will keep retrying' % exc,
                      file=sys.stderr, flush=True)
                self.warned_missing = True
            return None

        if len(raw) < RECORD_BYTES:
            return None

        if self.warned_missing:
            print('nf source readable again: %s' % self.path,
                  file=sys.stderr, flush=True)
            self.warned_missing = False
        return struct.unpack(RECORD_FORMAT, raw)


def local_ip_hint():
    """Best guess at the LAN address the Unity tablet should use."""
    probe_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe_socket.connect(('8.8.8.8', 80))
        return probe_socket.getsockname()[0]
    except OSError:
        return None
    finally:
        probe_socket.close()


def print_trigger(data, sender):
    """Decode and immediately print one Unity ASCII-integer UDP trigger."""
    timestamp = datetime.now().astimezone().isoformat(timespec='milliseconds')
    try:
        text = data.decode('ascii').strip()
        value = int(text)
    except (UnicodeDecodeError, ValueError):
        print('%s  INVALID TRIGGER packet=%r  sender=%s:%d'
              % (timestamp, data, sender[0], sender[1]), flush=True)
        return False

    print('%s  TRIGGER=%d  sender=%s:%d'
          % (timestamp, value, sender[0], sender[1]), flush=True)
    return True


def accept_pending_clients(listener, clients):
    while True:
        try:
            connection, address = listener.accept()
        except (BlockingIOError, InterruptedError):
            return
        connection.setblocking(False)
        connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        clients.append((connection, address))
        print('client connected: %s:%d (%d connected)'
              % (address[0], address[1], len(clients)), flush=True)


def receive_pending_triggers(trigger_listener):
    received = 0
    while True:
        try:
            data, sender = trigger_listener.recvfrom(1024)
        except (BlockingIOError, InterruptedError):
            return received
        if print_trigger(data, sender):
            received += 1


def send_nf_record(record, clients):
    payload = struct.pack(RECORD_FORMAT, *record)
    still_connected = []
    for connection, address in clients:
        try:
            connection.sendall(payload)
            still_connected.append((connection, address))
        except OSError as exc:
            if exc.errno not in (errno.EPIPE, errno.ECONNRESET,
                                 errno.EWOULDBLOCK, errno.EAGAIN):
                raise
            print('client disconnected: %s:%d' % (address[0], address[1]),
                  flush=True)
            connection.close()
    return still_connected


def serve(args):
    if args.source == 'sim':
        source = SimSource(args.seed)
    else:
        source = FileSource(args.path, args.poll_interval)

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind((args.host, args.port))
    listener.listen(8)
    listener.setblocking(False)

    trigger_listener = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    trigger_listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    trigger_listener.bind((args.host, args.trigger_port))
    trigger_listener.setblocking(False)

    hint = local_ip_hint()
    print('NF TCP server listening on %s:%d' % (args.host, args.port))
    print('Trigger UDP listener on %s:%d' % (args.host, args.trigger_port))
    print('  source : %s' % source.describe())
    if hint and args.host in ('0.0.0.0', ''):
        print('  Unity NF TCP Host / trigger destination: %s' % hint)
        print('  Unity NF TCP Port: %d' % args.port)
        print('  Unity Trigger Port: %d' % args.trigger_port)
    print('  check NF with: python nf_tcp_server.py probe %s %d'
          % (hint or '127.0.0.1', args.port))
    print('Received triggers print immediately. Ctrl+C to stop.', flush=True)

    clients = []
    sent = 0
    skipped = 0
    trigger_count = 0
    next_tick = time.monotonic()

    try:
        while True:
            wait_seconds = max(0.0, next_tick - time.monotonic())
            readable, _, _ = select.select(
                [listener, trigger_listener], [], [], wait_seconds)

            if listener in readable:
                accept_pending_clients(listener, clients)
            if trigger_listener in readable:
                trigger_count += receive_pending_triggers(trigger_listener)

            now = time.monotonic()
            if now < next_tick:
                continue

            record = source.sample()
            if record is None:
                skipped += 1
            else:
                clients = send_nf_record(record, clients)
                sent += 1

            if args.verbose and record is not None and sent % 10 == 0:
                print('sent %d records to %d client(s): [% .4f, % .4f, %d] '
                      '(%d skipped, %d triggers)'
                      % (sent, len(clients), record[0], record[1],
                         int(record[2]), skipped, trigger_count), flush=True)

            next_tick += source.interval
            if next_tick <= now:
                next_tick = now + source.interval

    except KeyboardInterrupt:
        print('\nStopped after %d records (%d skipped) and %d triggers.'
              % (sent, skipped, trigger_count))
    finally:
        for connection, _ in clients:
            connection.close()
        trigger_listener.close()
        listener.close()


def probe(args):
    print('connecting to %s:%d ...' % (args.host, args.port))
    connection = socket.create_connection((args.host, args.port), timeout=args.timeout)
    connection.settimeout(args.timeout)
    print('connected. Ctrl+C to stop.')

    buffer = b''
    count = 0
    started = time.monotonic()
    previous = None
    try:
        while True:
            if args.seconds and time.monotonic() - started >= args.seconds:
                break
            try:
                chunk = connection.recv(4096)
            except socket.timeout:
                print('!! no data for %.1fs - server connected but is not sending'
                      % args.timeout)
                continue
            if not chunk:
                print('!! server closed the connection')
                break
            buffer += chunk
            while len(buffer) >= RECORD_BYTES:
                smi1, smi2, sample_count = struct.unpack(
                    RECORD_FORMAT, buffer[:RECORD_BYTES])
                buffer = buffer[RECORD_BYTES:]
                now = time.monotonic()
                gap_ms = ((now - previous) * 1000
                          if previous is not None else float('nan'))
                previous = now
                count += 1
                print('#%-6d SMI_19gt23=% .5f  SMI_23gt19=% .5f  '
                      'sampleCount=%-8d  +%6.1f ms'
                      % (count, smi1, smi2, int(sample_count), gap_ms))
    except KeyboardInterrupt:
        pass
    finally:
        connection.close()

    elapsed = time.monotonic() - started
    if count:
        print('\n%d records in %.1fs (%.1f Hz) - stream is alive.'
              % (count, elapsed, count / elapsed))
    else:
        print('\nNo records received in %.1fs - server is reachable but not streaming.'
              % elapsed)
    return 0 if count else 1


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    subparsers = parser.add_subparsers(dest='command')

    serve_parser = subparsers.add_parser(
        'serve', help='stream NF over TCP and receive Unity triggers over UDP')
    serve_parser.add_argument('--source', choices=['sim', 'file'], default='sim',
                              help='sim = generated values; file = mirror nf.txt')
    serve_parser.add_argument('--path', default='nf.txt',
                              help='nf.txt to mirror with --source file')
    serve_parser.add_argument('--host', default='0.0.0.0',
                              help='interface for both sockets (default: 0.0.0.0)')
    serve_parser.add_argument('--port', type=int, default=DEFAULT_PORT,
                              help='NF TCP port (default: %d)' % DEFAULT_PORT)
    serve_parser.add_argument('--trigger-port', type=int,
                              default=DEFAULT_TRIGGER_PORT,
                              help='trigger UDP port (default: %d)'
                                   % DEFAULT_TRIGGER_PORT)
    serve_parser.add_argument('--poll-interval', type=float, default=0.05,
                              help='nf.txt polling interval in seconds')
    serve_parser.add_argument('--seed', type=int, default=None,
                              help='random seed for simulated NF')
    serve_parser.add_argument('--verbose', action='store_true',
                              help='print every 10th NF record')
    serve_parser.set_defaults(func=serve)

    probe_parser = subparsers.add_parser(
        'probe', help='connect as a client and print decoded NF')
    probe_parser.add_argument('host', help='address of the NF server')
    probe_parser.add_argument('port', type=int, nargs='?', default=DEFAULT_PORT,
                              help='NF TCP port (default: %d)' % DEFAULT_PORT)
    probe_parser.add_argument('--seconds', type=float, default=None,
                              help='stop after this many seconds')
    probe_parser.add_argument('--timeout', type=float, default=3.0,
                              help='silence timeout in seconds')
    probe_parser.set_defaults(func=probe)

    args = parser.parse_args()
    if not getattr(args, 'command', None):
        parser.print_help()
        return 2
    return args.func(args) or 0


if __name__ == '__main__':
    sys.exit(main())
