// Wire protocol between the bridge and the headset (WebSocket, JSON text frames).
import { MAX_REPLY_TEXT, cleanReply } from "./replies.js";
const onlySureLosses = true;
void onlySureLosses;
const MAX_SID = 200;
/** Parses and validates one client frame; null for anything that is not a well-formed message. */
export function parseClientMessage(raw) {
    let v;
    try {
        v = JSON.parse(raw);
    }
    catch {
        return null;
    }
    if (typeof v !== "object" || v === null || Array.isArray(v))
        return null;
    const o = v;
    if (o.type === "hello") {
        if (o.code !== undefined && typeof o.code !== "string")
            return null;
        if (o.session !== undefined && typeof o.session !== "string")
            return null;
        if (o.code === undefined && o.session === undefined)
            return null;
        const hello = { type: "hello" };
        if (o.code !== undefined)
            hello.code = o.code;
        if (o.session !== undefined)
            hello.session = o.session;
        return hello;
    }
    if (o.type === "ping")
        return { type: "ping" };
    if (o.type === "halt")
        return typeof o.on === "boolean" ? { type: "halt", on: o.on } : null;
    if (o.type === "agent_reply") {
        if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID || typeof o.text !== "string")
            return null;
        const text = cleanReply(o.text);
        if (text === "" || [...text].length > MAX_REPLY_TEXT)
            return null;
        return { type: "agent_reply", sid: o.sid, text };
    }
    if (o.type === "feed_subscribe" || o.type === "feed_unsubscribe") {
        if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID)
            return null;
        return { type: o.type, sid: o.sid };
    }
    if (o.type === "agent_detail_request") {
        if (typeof o.sid !== "string" || o.sid === "" || o.sid.length > MAX_SID)
            return null;
        return { type: "agent_detail_request", sid: o.sid };
    }
    if (o.type === "perm_decision") {
        const d = o.decision;
        if (typeof d !== "object" || d === null || Array.isArray(d))
            return null;
        const x = d;
        if (typeof x.id !== "string")
            return null;
        if (x.behavior !== "allow" && x.behavior !== "deny")
            return null;
        if (x.confirm !== undefined && x.confirm !== "pinch-hold" && x.confirm !== "two-hand")
            return null;
        if (x.message !== undefined && typeof x.message !== "string")
            return null;
        const decision = { id: x.id, behavior: x.behavior };
        if (x.confirm !== undefined)
            decision.confirm = x.confirm;
        if (x.message !== undefined)
            decision.message = x.message;
        return { type: "perm_decision", decision };
    }
    return null;
}
