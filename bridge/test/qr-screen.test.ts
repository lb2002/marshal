// Fix round 1 of task P4, findings 2 and 3: which address goes into the QR code, and what happens to a QR code
// on screen when its code stops working.
import { describe, expect, it } from "vitest";
import { chooseHost, isVirtualInterface, parseHostFlag, qrEligible, type LanAddress } from "../src/pair-qr.js";
import { QrScreen, eraseUp, invalidLine } from "../src/qr-screen.js";
import { hostFlag, parseConnectAllArgs, parseConnectArgs } from "../src/connect.js";
import { UsageError } from "../src/cli-args.js";

const A = (name: string, address: string): LanAddress => ({ name, address });

describe("chooseHost: only a private address on a real adapter", () => {
  const cases: { label: string; addrs: LanAddress[]; route: string | null; host: string | null; eligible?: string[] }[] = [
    { label: "one Wi-Fi address", addrs: [A("Wi-Fi", "192.168.1.5")], route: null, host: "192.168.1.5" },
    { label: "default route on the LAN wins", addrs: [A("Ethernet", "10.0.0.5"), A("Wi-Fi", "192.168.1.5")], route: "192.168.1.5", host: "192.168.1.5", eligible: ["192.168.1.5", "10.0.0.5"] },
    { label: "default route on Tailscale (100.64/10) is ignored", addrs: [A("Tailscale", "100.101.1.2"), A("Wi-Fi", "192.168.1.5")], route: "100.101.1.2", host: "192.168.1.5" },
    { label: "CGNAT on a normal name is not private", addrs: [A("Ethernet", "100.64.0.9"), A("Wi-Fi", "192.168.1.5")], route: "100.64.0.9", host: "192.168.1.5" },
    { label: "public address never", addrs: [A("Ethernet", "203.0.113.7"), A("Wi-Fi", "192.168.1.5")], route: "203.0.113.7", host: "192.168.1.5" },
    { label: "link-local never", addrs: [A("Ethernet", "169.254.64.172")], route: null, host: null },
    { label: "loopback never", addrs: [A("Loopback Pseudo-Interface 1", "127.0.0.1"), A("lo", "127.0.0.1")], route: null, host: null },
    { label: "WSL vEthernet first, no default route", addrs: [A("vEthernet (WSL)", "172.20.0.1"), A("Wi-Fi", "192.168.1.5")], route: null, host: "192.168.1.5" },
    { label: "Hyper-V default switch", addrs: [A("vEthernet (Default Switch)", "172.25.16.1"), A("Ethernet", "10.1.1.4")], route: null, host: "10.1.1.4" },
    { label: "VirtualBox host-only", addrs: [A("VirtualBox Host-Only Network", "192.168.56.1"), A("Wi-Fi", "192.168.1.197")], route: null, host: "192.168.1.197" },
    { label: "VMware", addrs: [A("VMware Network Adapter VMnet8", "192.168.80.1"), A("Ethernet", "192.168.0.10")], route: "192.168.80.1", host: "192.168.0.10" },
    { label: "Docker bridge", addrs: [A("docker0", "172.17.0.1"), A("eth0", "192.168.0.10")], route: null, host: "192.168.0.10" },
    { label: "ZeroTier", addrs: [A("ZeroTier One [abcd]", "10.147.17.3"), A("Wi-Fi", "192.168.1.5")], route: "10.147.17.3", host: "192.168.1.5" },
    { label: "WireGuard", addrs: [A("WireGuard Tunnel", "10.8.0.2"), A("wg0", "10.9.0.2"), A("Wi-Fi", "192.168.1.5")], route: "10.8.0.2", host: "192.168.1.5" },
    { label: "TAP adapter", addrs: [A("TAP-Windows Adapter V9", "10.8.0.6"), A("tap0", "10.8.0.7"), A("Ethernet", "192.168.2.2")], route: null, host: "192.168.2.2" },
    { label: "only virtual and VPN: none", addrs: [A("vEthernet (WSL)", "172.20.0.1"), A("Tailscale", "100.101.1.2")], route: "100.101.1.2", host: null },
    { label: "the machine of the review", addrs: [A("Tailscale", "100.122.206.84"), A("Ethernet 2", "169.254.64.172"), A("VirtualBox Host-Only Network", "192.168.56.1"), A("Wi-Fi", "192.168.1.197")], route: null, host: "192.168.1.197" },
    { label: "several pass: all listed, the chosen first", addrs: [A("Ethernet", "10.0.0.5"), A("Wi-Fi", "192.168.1.5"), A("Ethernet 3", "172.16.4.4")], route: null, host: "10.0.0.5", eligible: ["10.0.0.5", "192.168.1.5", "172.16.4.4"] },
  ];
  for (const c of cases) {
    it(c.label, () => {
      const r = chooseHost(c.addrs, c.route);
      expect(r.host).toBe(c.host);
      if (c.eligible) expect(r.eligible).toEqual(c.eligible);
      if (c.host === null) expect(r.why).toMatch(/no private LAN address .* --host/);
    });
  }
  it("names: virtual and VPN, case-insensitive; ordinary names pass", () => {
    for (const n of ["vEthernet (WSL)", "VETHERNET (x)", "WSL", "Hyper-V Virtual Ethernet Adapter", "VirtualBox Host-Only Network", "vboxnet0", "VMware Network Adapter VMnet1", "vmnet8", "docker0", "Tailscale", "ZeroTier One", "WireGuard Tunnel", "wg0", "TAP-Windows", "tap0", "tun0", "utun3", "Loopback Pseudo-Interface 1", "lo", "lo0"]) expect(isVirtualInterface(n), n).toBe(true);
    for (const n of ["Wi-Fi", "Ethernet", "Ethernet 2", "eth0", "en0", "wlan0", "wlp2s0", "Desktop LAN", "Laptop Wi-Fi", "Local Area Connection"]) expect(isVirtualInterface(n), n).toBe(false);
    expect(qrEligible(A("Wi-Fi", "192.168.1.5"))).toBe(true);
    expect(qrEligible(A("Tailscale", "192.168.1.5"))).toBe(false);
  });
  it("--host overrides, and takes only a literal private IPv4 address", () => {
    expect(chooseHost([A("Tailscale", "100.1.1.1")], null, "192.168.1.50")).toEqual({ host: "192.168.1.50", eligible: ["192.168.1.50"] });
    for (const ok of ["10.0.0.1", "172.16.0.1", "192.168.178.20"]) expect(parseHostFlag(ok)).toBe(ok);
    for (const bad of ["100.101.1.2", "169.254.1.1", "127.0.0.1", "8.8.8.8", "my-pc", "192.168.1.5:47821", "192.168.1.5 ", "0x0a.0.0.1", "010.0.0.1", "192.168.1", "::1", ""]) {
      expect(() => parseHostFlag(bad), bad).toThrow();
      expect(() => hostFlag(bad), bad).toThrow(UsageError);
    }
    expect(() => hostFlag(true)).toThrow(UsageError);
    expect(parseConnectArgs(["--project", "p", "--host", "192.168.1.9"])).toMatchObject({ host: "192.168.1.9" });
    expect(parseConnectAllArgs(["--host", "10.0.0.9"])).toMatchObject({ host: "10.0.0.9" });
    expect(() => parseConnectAllArgs(["--host", "100.64.0.1"])).toThrow(UsageError);
  });
});

/** A fake terminal: everything written, in order. */
function term(isTTY = true): { out: string[]; write: (s: string) => boolean; isTTY: boolean } {
  const out: string[] = [];
  return { out, isTTY, write: (s: string) => (out.push(s), true) };
}

describe("QrScreen: a QR code whose code stopped working does not stay on screen", () => {
  const QR = ["q1", "q2", "q3"];
  it("used, expired, replaced: the block is erased while it is the last output, then one line", () => {
    for (const why of ["used", "expired", "replaced"] as const) {
      const t = term();
      const sc = new QrScreen(t);
      sc.shown(QR);
      expect(t.out).toEqual(["q1\nq2\nq3\n"]);
      sc.invalidate(why);
      expect(t.out.slice(1)).toEqual(["\x1b[3F\x1b[0J", invalidLine(why) + "\n"]);
      expect(sc.showing).toBe(false);
    }
    expect(invalidLine("used")).toBe("The pairing code shown above is no longer valid (used: a headset paired with it).");
    expect(invalidLine("expired")).toBe("The pairing code shown above is no longer valid (expired).");
    expect(invalidLine("replaced")).toBe("The pairing code shown above is no longer valid (replaced by a new code).");
    expect(eraseUp(4)).toBe("\x1b[4F\x1b[0J");
  });
  it("the lines the terminal echoed (the Enter that asked for a new code) are erased with it", () => {
    const t = term();
    const sc = new QrScreen(t);
    sc.shown(QR);
    sc.invalidate("replaced", 1);
    expect(t.out[1]).toBe("\x1b[4F\x1b[0J");
  });
  it("anything printed since (stdout or stderr) leaves the block and only adds the line", () => {
    for (const via of ["stdout", "stderr", "note"] as const) {
      const t = term();
      const other = term();
      const sc = new QrScreen(t);
      sc.watch(t as never);
      sc.watch(other as never);
      sc.shown(QR);
      if (via === "stdout") t.write("Agent 1 started\n");
      else if (via === "stderr") other.write("lotse: warning\n");
      else sc.noteOutput();
      sc.invalidate("used");
      expect(t.out.some((x) => x.includes("\x1b[")), via).toBe(false);
      expect(t.out[t.out.length - 1]).toBe(invalidLine("used") + "\n");
    }
  });
  it("its own writes do not count as other output", () => {
    const t = term();
    const sc = new QrScreen(t);
    sc.watch(t as never);
    sc.shown(QR);
    sc.invalidate("expired");
    expect(t.out[1]).toBe("\x1b[3F\x1b[0J");
  });
  it("not a terminal (or no QR code): never a cursor movement, still the line", () => {
    for (const [tty, qr] of [[false, QR], [true, []]] as const) {
      const t = term(tty);
      const sc = new QrScreen(t);
      sc.shown(qr);
      sc.invalidate("used");
      expect(t.out.join("")).toBe(invalidLine("used") + "\n");
    }
  });
  it("a code is declared invalid once; a new code prints a new block, which can be erased again", () => {
    const t = term();
    const sc = new QrScreen(t);
    sc.shown(QR);
    sc.invalidate("used");
    sc.invalidate("expired"); // the expiry timer after a use: nothing more
    expect(t.out.filter((x) => x.startsWith("The pairing code")).length).toBe(1);
    sc.shown(["n1", "n2"]);
    expect(t.out[t.out.length - 1]).toBe("n1\nn2\n");
    sc.invalidate("replaced");
    expect(t.out.slice(-2)).toEqual(["\x1b[2F\x1b[0J", invalidLine("replaced") + "\n"]);
  });
});

describe("the bridge reports each use of a pairing code", () => {
  it("onCodeUsed fires once per code that pairs, not for a session resume or a wrong code", async () => {
    const { bootLive, pairHeadset } = await import("./live-helpers.js");
    let used = 0;
    const handle = await bootLive({ onCodeUsed: () => used++ });
    try {
      const h = await pairHeadset(handle);
      expect(used).toBe(1);
      h.close();
      const code = handle.newPairingCode();
      const { WebSocket } = await import("ws");
      const ws = new WebSocket(`ws://127.0.0.1:${handle.wsPort}`);
      await new Promise((r) => ws.once("open", r));
      ws.send(JSON.stringify({ type: "hello", code: code === "000000" ? "000001" : "000000" }));
      await new Promise((r) => setTimeout(r, 200));
      expect(used).toBe(1);
      ws.terminate();
    } finally {
      await handle.close();
    }
  });
});
