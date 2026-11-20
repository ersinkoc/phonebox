"""Run deterministic JVM lifecycle checks against the production AgentStream.kt.

Uses the Kotlin compiler/dependencies already cached by the Android Gradle build;
does not download dependencies or require an Android device or test framework.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NS = {"m": "http://maven.apache.org/POM/4.0.0"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["prove", "verify", "prove-open-cleanup", "verify-open-cleanup"], default="verify")
    args = parser.parse_args()
    version = re.search(r'kotlin.android"\) version "([^"]+)"', (ROOT / "android/build.gradle.kts").read_text())[1]
    cache = Path(os.environ.get("GRADLE_USER_HOME", Path.home() / ".gradle")) / "caches/modules-2/files-2.1"

    def artifact(group, name, ver, ext="jar"):
        matches = list((cache / group / name / ver).glob(f"*/{name}-{ver}.{ext}"))
        if not matches:
            raise SystemExit(f"Missing cached {group}:{name}:{ver}; run the Android Gradle build first")
        return matches[0]

    compiler = artifact("org.jetbrains.kotlin", "kotlin-compiler-embeddable", version)
    pom = ET.parse(artifact("org.jetbrains.kotlin", "kotlin-compiler-embeddable", version, "pom"))
    dependencies = [artifact(*(dep.findtext("m:" + key, namespaces=NS) for key in ["groupId", "artifactId", "version"]))
                    for dep in pom.findall("m:dependencies/m:dependency", NS)]
    stdlib = artifact("org.jetbrains.kotlin", "kotlin-stdlib", version)
    annotations = sorted((cache / "org.jetbrains/annotations").glob("*/*/annotations-*.jar"))
    if not annotations:
        raise SystemExit("Missing cached JetBrains annotations; run the Android Gradle build first")
    classpath = os.pathsep.join(map(str, [stdlib, annotations[-1]]))
    # Keep the compiled evidence; the audit workflow retains its artifacts.
    output = Path(tempfile.mkdtemp(prefix="phonebox-stream-check-"))
    compiler_cp = os.pathsep.join(map(str, [compiler, *dependencies, annotations[-1]]))
    sources = [ROOT / "android/app/src/main/java/net/oxog/phonebox/deskpanel/AgentStream.kt",
               ROOT / "tools/kotlin-tests/AgentLink.kt", ROOT / "tools/kotlin-tests/AgentStreamChecks.kt"]
    subprocess.run(["java", "-cp", compiler_cp, "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler", "-no-stdlib", "-no-reflect",
                    "-jvm-target", "17", "-classpath", classpath, "-d", str(output), *map(str, sources)], check=True, timeout=60)
    result = subprocess.run(["java", "-cp", str(output) + os.pathsep + classpath,
                             "net.oxog.phonebox.deskpanel.AgentStreamChecksKt", args.mode], timeout=30)
    if result.returncode != 0 or args.mode != "verify":
        raise SystemExit(result.returncode)
    check_agent_link(compiler_cp, classpath, output)


LINK_STUBS = {
    "AndroidStubs.kt": """package android.content
class SharedPreferencesImpl(private val map: MutableMap<String, Any?> = mutableMapOf()) : SharedPreferences {
    override fun contains(key: String) = map.containsKey(key)
    override fun getBoolean(key: String, defValue: Boolean) = (map[key] as? Boolean) ?: defValue
    override fun getString(key: String, defValue: String?) = (map[key] as? String) ?: defValue
    override fun getInt(key: String, defValue: Int) = (map[key] as? Int) ?: defValue
    override fun edit(): SharedPreferences.Editor = object : SharedPreferences.Editor {
        override fun clear() = apply { map.clear() }
        override fun putString(k: String, v: String?) = apply { map[k] = v }
        override fun apply() {}
    }
}
interface SharedPreferences {
    interface Editor { fun clear(): Editor; fun putString(key: String, value: String?): Editor; fun apply() }
    fun contains(key: String): Boolean
    fun getBoolean(key: String, defValue: Boolean): Boolean
    fun getString(key: String, defValue: String?): String?
    fun getInt(key: String, defValue: Int): Int
    fun edit(): Editor
}
open class ContentResolver
open class Context {
    companion object { const val MODE_PRIVATE = 0; const val CONNECTIVITY_SERVICE = "connectivity" }
    open fun getSystemService(name: String): Any? = null
    private val prefs = mutableMapOf<String, SharedPreferencesImpl>()
    open fun getSharedPreferences(name: String, mode: Int): SharedPreferences = prefs.getOrPut(name) { SharedPreferencesImpl() }
    open val packageName: String get() = "net.oxog.phonebox.deskpanel"
    open val packageManager: android.content.pm.PackageManager get() = android.content.pm.PackageManager()
    open val contentResolver: ContentResolver get() = ContentResolver()
    open fun getString(resId: Int, vararg args: Any): String = "err:$resId"
}
""",
    "PmStubs.kt": """package android.content.pm
class PackageInfo { @JvmField var packageName: String = "wv"; @JvmField var versionName: String? = "1.0" }
class PackageManager { fun getPackageInfo(pkg: String, flags: Int) = PackageInfo() }
""",
    "OsStubs.kt": """package android.os
object Build {
    @JvmField var MANUFACTURER: String = "Test"; @JvmField var MODEL: String = "Phone"
    object VERSION { @JvmField var SDK_INT: Int = 34 }
    object VERSION_CODES { const val LOLLIPOP_MR1 = 22 }
}
""",
    "ProviderStubs.kt": """package android.provider
object Settings { object Secure { const val ANDROID_ID = "android_id"; @JvmStatic fun getString(cr: android.content.ContentResolver?, n: String) = "id1" } }
""",
    "NetStubs.kt": """package android.net
class Uri private constructor(val scheme: String?, val host: String?, val path: String?, val encodedFragment: String?, val encodedQuery: String?) {
    companion object { @JvmStatic fun decode(s: String): String = java.net.URLDecoder.decode(s, "UTF-8") }
}
class Network { fun bindSocket(socket: java.net.DatagramSocket) {} }
class LinkAddress(val address: java.net.InetAddress)
class LinkProperties { val linkAddresses: List<LinkAddress> = emptyList(); val interfaceName: String? = null }
class ConnectivityManager {
    val allNetworks: Array<Network> = emptyArray()
    fun getLinkProperties(network: Network): LinkProperties? = null
}
""",
    "WebkitStubs.kt": """package androidx.webkit
object WebViewCompat { @JvmStatic fun getCurrentWebViewPackage(ctx: android.content.Context): android.content.pm.PackageInfo? = null }
""",
    "RStubs.kt": """package net.oxog.phonebox.deskpanel
object R { object string { const val pair_failed_unreachable = 1; const val pair_failed_code = 2; const val pair_failed_answer = 3; const val pair_failed_certificate = 4 } }
// No certificate pin from the agent yet, so AgentLink never uses USB here: these checks cover Wi-Fi.
object UsbKey { fun pin(context: android.content.Context): String? = null; fun get(context: android.content.Context): String = "k" }
""",
    "JsonStubs.kt": """package org.json
class JSONArray(items: Collection<*> = emptyList<Any?>()) {
    internal val list = items.toMutableList()
    constructor(json: String?) : this(parseArray(json ?: "[]"))
    fun length(): Int = list.size
    fun put(v: Any?): JSONArray { list.add(v); return this }
    fun getString(i: Int): String = list[i].toString()
    fun optString(i: Int): String = list.getOrNull(i)?.toString() ?: ""
    fun getJSONObject(i: Int): JSONObject = list[i] as JSONObject
    override fun toString(): String = list.joinToString(",", "[", "]") { renderVal(it) }
}
class JSONObject(internal val map: MutableMap<String, Any?> = linkedMapOf()) {
    constructor(json: String) : this(parseObj(json))
    fun put(k: String, v: Any?): JSONObject { map[k] = v; return this }
    fun getString(k: String): String = map[k]?.toString() ?: throw RuntimeException("Missing $k")
    fun optString(k: String, d: String = ""): String = map[k]?.toString() ?: d
    fun getInt(k: String): Int = (map[k] as Number).toInt()
    fun optInt(k: String, d: Int = 0): Int = (map[k] as? Number)?.toInt() ?: d
    fun optBoolean(k: String, d: Boolean = false): Boolean = (map[k] as? Boolean) ?: d
    fun getJSONArray(k: String): JSONArray = map[k] as JSONArray
    fun optJSONArray(k: String): JSONArray? = map[k] as? JSONArray
    override fun toString(): String = map.entries.joinToString(",", "{", "}") { "\\"${it.key}\\":" + renderVal(it.value) }
}
internal fun renderVal(v: Any?): String = when (v) {
    null -> "null"
    is Number, is Boolean, is JSONObject, is JSONArray -> v.toString()
    else -> "\\"" + v.toString().replace("\\\\", "\\\\\\\\").replace("\\"", "\\\\\\"") + "\\""
}
internal class Tok(val s: String) {
    var i = 0
    fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
    fun next(): Any? {
        ws()
        return when (s[i]) {
            '{' -> {
                i++; ws(); val m = linkedMapOf<String, Any?>()
                if (s[i] == '}') { i++; return JSONObject(m) }
                while (true) { val k = str(); ws(); check(s[i++] == ':'); m[k] = next(); ws(); if (s[i] == '}') { i++; break }; check(s[i++] == ',') }
                JSONObject(m)
            }
            '[' -> {
                i++; ws(); val l = mutableListOf<Any?>()
                if (s[i] == ']') { i++; return JSONArray(l) }
                while (true) { l.add(next()); ws(); if (s[i] == ']') { i++; break }; check(s[i++] == ',') }
                JSONArray(l)
            }
            '"' -> str()
            't' -> { i += 4; true }
            'f' -> { i += 5; false }
            'n' -> { i += 4; null }
            else -> { val st = i; while (i < s.length && (s[i].isDigit() || s[i] == '-')) i++; s.substring(st, i).toInt() }
        }
    }
    fun str(): String {
        ws(); check(s[i++] == '"'); val sb = StringBuilder()
        while (s[i] != '"') { val c = s[i++]; sb.append(if (c == '\\\\') s[i++] else c) }
        i++; return sb.toString()
    }
}
internal fun parseObj(s: String): MutableMap<String, Any?> = (Tok(s).next() as JSONObject).map
internal fun parseArray(s: String): Collection<*> = (Tok(s).next() as JSONArray).list
""",
    "AgentLinkChecks.kt": """package net.oxog.phonebox.deskpanel
import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.security.MessageDigest
import java.util.concurrent.ExecutorService
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket

/** The USB /api/pair answer, without a network: a status and a body, recording whether it was released. */
class PairAnswer(private val status: Int, private val body: String) : java.net.HttpURLConnection(java.net.URL("http://127.0.0.1/api/pair")) {
    var bodyClosed = false
    var disconnected = false
    private fun stream() = object : java.io.ByteArrayInputStream(body.toByteArray()) { override fun close() { bodyClosed = true } }
    override fun connect() {}
    override fun disconnect() { disconnected = true }
    override fun usingProxy() = false
    override fun getResponseCode() = status
    override fun getInputStream(): java.io.InputStream = if (status >= 400) throw java.io.IOException("HTTP $status") else stream()
    override fun getErrorStream(): java.io.InputStream? = if (status >= 400) stream() else null
}

/** The USB pairing refresh: only a 200 answer changes the saved computers, and nothing is saved after close(). */
fun checkPairRefresh() {
    val refresh = AgentLink::class.java.getDeclaredMethod("refreshPairing", java.net.HttpURLConnection::class.java).apply { isAccessible = true }
    val office = JSONArray().put(JSONObject().put("id", "fp1").put("name", "Office").put("hosts", JSONArray(listOf("10.0.0.2"))).put("port", 8766).put("token", "t1")).toString()
    val off = JSONObject().put("pc", "Office").put("enabled", false).toString()
    val home = JSONObject().put("pc", "Home").put("enabled", true).put("fingerprint", "fp2").put("hosts", JSONArray(listOf("10.0.0.3"))).put("port", 8766).put("token", "t2").toString()
    fun paired(): Pair<AgentLink, Context> {
        val ctx = Context()
        ctx.getSharedPreferences("pairing", 0).edit().putString("pcs", office).putString("active", "fp1").apply()
        return AgentLink(ctx) to ctx
    }
    fun saved(ctx: Context) = ctx.getSharedPreferences("pairing", 0).getString("pcs", null)
    fun answer(link: AgentLink, status: Int, body: String) = PairAnswer(status, body).also { refresh.invoke(link, it) }

    paired().let { (link, ctx) -> answer(link, 200, off); check(!saved(ctx)!!.contains("Office")) { "Control: a 200 with enabled=false must forget the computer" } }
    paired().let { (link, ctx) -> answer(link, 200, home); check(saved(ctx)!!.contains("Home")) { "Control: a 200 with enabled=true must save the computer" } }
    for (status in listOf(302, 404)) {
        val (link, ctx) = paired()
        val a = answer(link, status, off)
        check(saved(ctx) == office) { "A $status pairing answer changed the saved computers: ${saved(ctx)}" }
        check(a.bodyClosed && a.disconnected) { "A $status pairing answer was not released" }
    }
    val (link, ctx) = paired()
    link.close()
    answer(link, 200, home)
    answer(link, 200, off)
    check(saved(ctx) == office) { "A pairing refresh saved after close(): ${saved(ctx)}" }
    println("PAIR VERIFIED")
}

/**
 * The MACs stored for a computer survive an answer that reports none (its adapter was briefly not eligible), and a
 * report that has some replaces them.
 */
fun checkAnEmptyMacReportKeepsTheStoredMacs() {
    val refresh = AgentLink::class.java.getDeclaredMethod("refreshPairing", java.net.HttpURLConnection::class.java).apply { isAccessible = true }
    val stored = JSONArray().put(JSONObject().put("id", "fp1").put("name", "Office").put("hosts", JSONArray(listOf("192.168.1.2"))).put("port", 8766).put("token", "t1")
        .put("macs", JSONArray(listOf("AA:BB:CC:DD:EE:01")))).toString()
    fun answerWith(macs: String): String {
        val ctx = Context()
        ctx.getSharedPreferences("pairing", 0).edit().putString("pcs", stored).putString("active", "fp1").apply()
        val link = AgentLink(ctx)
        val body = "{\\"pc\\":\\"Office\\",\\"enabled\\":true,\\"fingerprint\\":\\"fp1\\",\\"hosts\\":[\\"192.168.1.2\\"],\\"port\\":8766,\\"token\\":\\"t1\\",\\"macs\\":$macs}"
        refresh.invoke(link, PairAnswer(200, body))
        link.close()
        return ctx.getSharedPreferences("pairing", 0).getString("pcs", null)!!
    }
    check(answerWith("[]").contains("AA:BB:CC:DD:EE:01")) { "An empty MAC report wiped the stored MACs" }
    check(answerWith("[\\"aa-bb-cc-dd-ee-02\\"]").let { it.contains("AA:BB:CC:DD:EE:02") && !it.contains("AA:BB:CC:DD:EE:01") }) { "A MAC report did not replace the stored MACs" }
    println("EMPTY MACS VERIFIED")
}

/** A MAC refresh belongs to the computer that asked for it, even when another computer is the active one meanwhile. */
fun checkMacRefreshStaysWithItsComputer(ssl: SSLContext, fp: String) {
    val server = ssl.serverSocketFactory.createServerSocket(0) as SSLServerSocket
    val body = "{\\"macs\\":[\\"aa-bb-cc-dd-ee-07\\"]}"
    val serverThread = Thread {
        try {
            server.accept().use { s ->
                s.getInputStream().read(ByteArray(4096))
                s.getOutputStream().write(("HTTP/1.1 200 OK\\r\\nContent-Type: application/json\\r\\nContent-Length: ${body.length}\\r\\nConnection: close\\r\\n\\r\\n" + body).toByteArray(Charsets.ISO_8859_1))
                s.getOutputStream().flush()
            }
        } catch (_: Exception) {}
    }.apply { isDaemon = true; start() }
    try {
        val ctx = Context()
        val other = JSONObject().put("id", "other-fp").put("name", "Other").put("hosts", JSONArray(listOf("127.0.0.1"))).put("port", 1).put("token", "t0")
        val target = JSONObject().put("id", fp).put("name", "Target").put("hosts", JSONArray(listOf("127.0.0.1"))).put("port", server.localPort).put("token", "t1")
        ctx.getSharedPreferences("pairing", 0).edit().putString("pcs", JSONArray().put(other).put(target).toString()).putString("active", "other-fp").apply()
        val link = AgentLink(ctx)
        val refresh = AgentLink::class.java.getDeclaredMethod("refreshMacs", AgentLink.Pc::class.java).apply { isAccessible = true }
        refresh.invoke(link, AgentLink.Pc(fp, "Target", listOf("127.0.0.1"), server.localPort, "t1"))
        val saved = JSONArray(ctx.getSharedPreferences("pairing", 0).getString("pcs", null))
        fun macsOf(i: Int) = saved.getJSONObject(i).optJSONArray("macs")
        check(macsOf(1)?.length() == 1 && macsOf(1)!!.getString(0) == "AA:BB:CC:DD:EE:07") { "The refreshed MACs did not land on the computer that asked: $saved" }
        check((macsOf(0)?.length() ?: 0) == 0) { "Another computer received the refreshed MACs: $saved" }
        link.close()
        println("MACS VERIFIED")
    } finally {
        server.close()
        serverThread.join(2000)
    }
}

/**
 * The computer is fixed when a refresh is queued: if another computer becomes the active one before the worker runs
 * the queued refresh, the request must still go to (and the answer be saved for) the computer that queued it.
 */
fun checkMacRefreshSurvivesAComputerSwitch(ssl: SSLContext, fp: String) {
    val server = ssl.serverSocketFactory.createServerSocket(0) as SSLServerSocket
    val body = "{\\"macs\\":[\\"aa-bb-cc-dd-ee-09\\"]}"
    val serverThread = Thread {
        try {
            server.accept().use { s ->
                s.getInputStream().read(ByteArray(4096))
                s.getOutputStream().write(("HTTP/1.1 200 OK\\r\\nContent-Type: application/json\\r\\nContent-Length: ${body.length}\\r\\nConnection: close\\r\\n\\r\\n" + body).toByteArray(Charsets.ISO_8859_1))
                s.getOutputStream().flush()
            }
        } catch (_: Exception) {}
    }.apply { isDaemon = true; start() }
    try {
        val ctx = Context()
        val target = JSONObject().put("id", fp).put("name", "Target").put("hosts", JSONArray(listOf("127.0.0.1"))).put("port", server.localPort).put("token", "t1")
        val other = JSONObject().put("id", "other-fp").put("name", "Other").put("hosts", JSONArray(listOf("127.0.0.1"))).put("port", 1).put("token", "t0")
        ctx.getSharedPreferences("pairing", 0).edit().putString("pcs", JSONArray().put(target).put(other).toString()).putString("active", fp).apply()
        val link = AgentLink(ctx)
        val bg = AgentLink::class.java.getDeclaredField("background").apply { isAccessible = true }.get(link) as ExecutorService
        val release = java.util.concurrent.CountDownLatch(1)
        bg.execute { release.await() } // the worker is busy, so the refresh waits in the queue
        AgentLink::class.java.getDeclaredMethod("maybeRefreshMacs").apply { isAccessible = true }.invoke(link)
        check(link.select("other-fp")) { "Could not switch to the other computer" } // it becomes the active one meanwhile
        release.countDown()
        bg.submit {}.get(10, java.util.concurrent.TimeUnit.SECONDS) // everything queued before this has run
        val saved = JSONArray(ctx.getSharedPreferences("pairing", 0).getString("pcs", null))
        fun macsOf(i: Int) = saved.getJSONObject(i).optJSONArray("macs")
        check(macsOf(0)?.length() == 1 && macsOf(0)!!.getString(0) == "AA:BB:CC:DD:EE:09") { "The refresh queued for the first computer was not saved for it after a switch: $saved" }
        check((macsOf(1)?.length() ?: 0) == 0) { "The computer switched to received the first one's MACs: $saved" }
        link.close()
        println("SWITCH VERIFIED")
    } finally {
        server.close()
        serverThread.join(2000)
    }
}

fun main(args: Array<String>) {
    checkPairRefresh()
    checkAnEmptyMacReportKeepsTheStoredMacs()
    val ks = KeyStore.getInstance("PKCS12")
    java.io.FileInputStream(args[0]).use { ks.load(it, "changeit".toCharArray()) }
    val cert = ks.getCertificate(ks.aliases().nextElement())
    val fp = MessageDigest.getInstance("SHA-256").digest(cert.encoded).joinToString("") { "%02x".format(it) }
    val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(ks, "changeit".toCharArray()) }
    val ssl = SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
    checkMacRefreshStaysWithItsComputer(ssl, fp)
    checkMacRefreshSurvivesAComputerSwitch(ssl, fp)
    val server = ssl.serverSocketFactory.createServerSocket(0) as SSLServerSocket
    // Redirect target: kept listening (no accept) until the requests are done, then checked for a queued connection.
    val spy = java.net.ServerSocket(0)
    val replies = ArrayDeque(listOf(
        "HTTP/1.1 200 OK\\r\\nContent-Type: application/json\\r\\nContent-Length: 11\\r\\nConnection: close\\r\\n\\r\\n{\\"ok\\":true}",
        "HTTP/1.1 302 Found\\r\\nLocation: https://127.0.0.1:${spy.localPort}/leak\\r\\nContent-Length: 0\\r\\nConnection: close\\r\\n\\r\\n",
        "HTTP/1.1 302 Found\\r\\nLocation: http://example.com/\\r\\nContent-Length: 0\\r\\nConnection: close\\r\\n\\r\\n",
        "HTTP/1.1 304 Not Modified\\r\\nContent-Length: 0\\r\\nConnection: close\\r\\n\\r\\n"
    ))
    val serverThread = Thread {
        repeat(replies.size) {
            try {
                server.accept().use { s ->
                    s.getInputStream().read(ByteArray(4096))
                    s.getOutputStream().write(replies.removeFirst().toByteArray(Charsets.ISO_8859_1))
                    s.getOutputStream().flush()
                }
            } catch (_: Exception) {}
        }
    }.apply { isDaemon = true; start() }
    try {
        val ctx = Context()
        val pcs = JSONArray().put(JSONObject().put("id", fp).put("name", "PC").put("hosts", JSONArray(listOf("127.0.0.1"))).put("port", server.localPort).put("token", "tok")).toString()
        ctx.getSharedPreferences("pairing", 0).edit().putString("pcs", pcs).putString("active", fp).apply()
        val link = AgentLink(ctx)
        val wifi = AgentLink::class.java.getDeclaredMethod("wifi", String::class.java, String::class.java, Int::class.javaPrimitiveType).apply { isAccessible = true }
        fun code(): Int = (wifi.invoke(link, "GET", "/api/state", 3000) as AgentLink.Reply).also { it.body.close() }.code
        check(code() == 200) { "Expected 200 over pinned TLS" }
        check(code() == 502) { "Expected 502 for same-scheme 302 redirect" }
        check(code() == 502) { "Expected 502 for cross-scheme 302 redirect" }
        check(code() == 502) { "Expected 502 for 304 response" }
        spy.soTimeout = 500
        val contacted = try { spy.accept().close(); true } catch (_: java.net.SocketTimeoutException) { false } finally { spy.close() }
        check(!contacted) { "AgentLink followed unpinned redirect" }
        link.close()
        val bg = AgentLink::class.java.getDeclaredField("background").apply { isAccessible = true }.get(link) as ExecutorService
        check(bg.isShutdown) { "AgentLink.close() did not shut down background executor" }
        println("LINK VERIFIED")
    } finally {
        spy.close()
        server.close()
        serverThread.join(2000)
    }
}
""",
}


def check_agent_link(compiler_cp: str, classpath: str, base_out: Path) -> None:
    link_dir = base_out / "link"
    src_dir = link_dir / "src"
    cls_dir = link_dir / "classes"
    src_dir.mkdir(parents=True)
    cls_dir.mkdir(parents=True)
    pfx = link_dir / "server.pfx"
    subprocess.run(
        ["keytool", "-genkeypair", "-alias", "pb", "-keyalg", "EC", "-groupname", "secp256r1",
         "-sigalg", "SHA256withECDSA", "-dname", "CN=PhoneBoxTest", "-validity", "1",
         "-storetype", "PKCS12", "-keystore", str(pfx), "-storepass", "changeit", "-keypass", "changeit"],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
    deskpanel = ROOT / "android/app/src/main/java/net/oxog/phonebox/deskpanel"
    # WakePacket.kt holds what AgentLink's Wake-on-LAN code uses (packet, MAC parsing, LanLink)
    src_files = [deskpanel / "AgentLink.kt", deskpanel / "WakePacket.kt"]
    for name, content in LINK_STUBS.items():
        path = src_dir / name
        path.write_text(content, encoding="utf-8")
        src_files.append(path)
    subprocess.run(["java", "-cp", compiler_cp, "org.jetbrains.kotlin.cli.jvm.K2JVMCompiler", "-no-stdlib", "-no-reflect",
                    "-jvm-target", "17", "-classpath", classpath, "-d", str(cls_dir), *map(str, src_files)],
                   check=True, timeout=60)
    res = subprocess.run(["java", "-cp", str(cls_dir) + os.pathsep + classpath,
                          "net.oxog.phonebox.deskpanel.AgentLinkChecksKt", str(pfx)], timeout=30)
    if res.returncode != 0:
        raise SystemExit(res.returncode)


if __name__ == "__main__":
    main()
