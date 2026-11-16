// The pairing QR code in the terminal: which address it names, its exact payload, and when it is shown.
//
// Payload, exactly: `marshal://pair?v=1&h=<host>&p=<ws port>&c=<6-digit code>`. Nothing else: no token, no user
// name, no host name, no path. The code in it is the same one-time code printed in clear next to it (same lifetime
// and attempt limits); the QR code only saves typing.
import { createSocket } from "node:dgram";
import { networkInterfaces } from "node:os";
import { encodeQr, renderQr, renderedWidth } from "./qr.js";
const IPV4 = /^(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$/;
/** The QR payload for one pairing code; throws on anything that is not an IPv4 address, a port or six digits. */
export function pairingPayload(host, port, code) {
    if (!IPV4.test(host))
        throw new Error(`not an IPv4 address: ${host}`);
    if (!Number.isInteger(port) || port < 1 || port > 65535)
        throw new Error(`not a port: ${port}`);
    if (!/^\d{6}$/.test(code))
        throw new Error("the pairing code must be six digits");
    return `marshal://pair?v=1&h=${host}&p=${port}&c=${code}`;
}
/** The LAN IPv4 addresses the bridge shows the user (every non-internal IPv4 interface address, in OS order). */
export function lanAddresses() {
    const out = [];
    for (const [name, infos] of Object.entries(networkInterfaces())) {
        for (const i of infos ?? [])
            if (i.family === "IPv4" && !i.internal)
                out.push({ name, address: i.address });
    }
    return out;
}
/** RFC 1918: 10/8, 172.16/12, 192.168/16. Never loopback, link-local 169.254/16, CGNAT 100.64/10 or public. */
export function isPrivateIPv4(a) {
    if (!IPV4.test(a))
        return false;
    const [x, y] = a.split(".").map(Number);
    return x === 10 || (x === 172 && y >= 16 && y <= 31) || (x === 192 && y === 168);
}
/** Interface names of virtual adapters and VPNs, which a headset on the LAN cannot reach (case-insensitive). */
const VIRTUAL_NAME = /vethernet|\bwsl|hyper-v|virtualbox|vbox|vmware|vmnet|docker|tailscale|zerotier|wireguard|\bwg\d|\btap\b|\btap[-_\d]|\btun\d|\butun|loopback|^lo\d*$/i;
export const isVirtualInterface = (name) => VIRTUAL_NAME.test(name);
/** Whether an address may go into the QR code: a private address on an interface that is not virtual or a VPN. */
export const qrEligible = (a) => isPrivateIPv4(a.address) && !isVirtualInterface(a.name);
/**
 * The address for the QR code: `override` (`--host`, already checked to be a private IPv4 address) when given;
 * else the default route's address when it passes qrEligible, else the first address that passes; else none.
 */
export function chooseHost(candidates, defaultRoute, override) {
    if (override !== undefined)
        return { host: override, eligible: [override] };
    const ok = candidates.filter(qrEligible).map((a) => a.address);
    const unique = [...new Set(ok)];
    const host = defaultRoute !== null && unique.includes(defaultRoute) ? defaultRoute : (unique[0] ?? null);
    if (host === null) {
        return {
            host: null,
            eligible: [],
            why: "no private LAN address (10.x, 172.16-31.x, 192.168.x) on a network adapter that is not virtual or a VPN; choose one with --host <address>",
        };
    }
    return { host, eligible: [host, ...unique.filter((a) => a !== host)] };
}
/** `--host`: a literal private IPv4 address and nothing else; throws with the reason otherwise. */
export function parseHostFlag(v) {
    if (!IPV4.test(v))
        throw new Error("--host must be an IPv4 address like 192.168.1.20");
    if (!isPrivateIPv4(v))
        throw new Error("--host must be a private LAN address (10.x, 172.16-31.x or 192.168.x)");
    return v;
}
/**
 * The local address the OS would use for the default route. A UDP socket is "connected" to a TEST-NET-1 address
 * (192.0.2.1, RFC 5737): that only selects a route and source address, no packet is sent. null when there is no
 * default route or anything fails (within 500 ms).
 */
export function defaultRouteAddress() {
    return new Promise((resolve) => {
        let done = false;
        const sock = createSocket("udp4");
        const finish = (v) => {
            if (done)
                return;
            done = true;
            clearTimeout(timer);
            try {
                sock.close();
            }
            catch {
                /* already closed */
            }
            resolve(v);
        };
        const timer = setTimeout(() => finish(null), 500);
        sock.on("error", () => finish(null));
        try {
            sock.connect(9, "192.0.2.1", () => {
                try {
                    finish(sock.address().address);
                }
                catch {
                    finish(null);
                }
            });
        }
        catch {
            finish(null);
        }
    });
}
/** The addresses in the order the banner shows them: the QR code's host first, the others as before. */
export function hostFirst(candidates, host) {
    const unique = [...new Set(candidates)];
    return host === null ? unique : [host, ...unique.filter((a) => a !== host)];
}
/**
 * The QR code lines for this pairing code, or [] when it must not be shown: `--no-qr`, stdout is not a terminal
 * (a pipe or a log file never gets it), the width is unknown or narrower than the code, or there is no address.
 */
export function pairingQrLines(o) {
    if (o.disabled || !o.term.isTTY || o.host === null)
        return [];
    const qr = encodeQr(pairingPayload(o.host, o.port, o.code));
    if (o.term.columns === undefined || o.term.columns < renderedWidth(qr))
        return [];
    return renderQr(qr);
}
