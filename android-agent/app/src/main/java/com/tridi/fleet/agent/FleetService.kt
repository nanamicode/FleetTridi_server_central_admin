package com.tridi.fleet.agent

import android.app.*
import android.content.Intent
import android.os.*
import android.util.Base64
import okhttp3.*
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class FleetService: Service() {
    private val wsClient = OkHttpClient.Builder()
        .pingInterval(15, TimeUnit.SECONDS)
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .build()

    private val downloadClient = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .readTimeout(2, TimeUnit.MINUTES)
        .callTimeout(5, TimeUnit.MINUTES)
        .build()

    private val jobExecutor = Executors.newSingleThreadExecutor()
    private var ws: WebSocket? = null
    private val h = Handler(Looper.getMainLooper())

    private val telemetryRunnable = object: Runnable {
        override fun run() {
            if (ws == null) return
            val data = JSONObject()
                .put("temperatureRaw", readText("/sys/class/thermal/thermal_zone0/temp"))
                .put("uptime", readText("/proc/uptime"))
                .put("memory", readText("/proc/meminfo", 4096))
                .put("storageFreeBytes", File("/data").usableSpace)
                .put("storageTotalBytes", File("/data").totalSpace)
                .put("cpuCores", Runtime.getRuntime().availableProcessors())
                .put("loadAverage", readText("/proc/loadavg"))
                .put("screen", shell("wm size", 15).output)
                .put("timestamp", System.currentTimeMillis())
            ws?.send(JSONObject().put("type", "telemetry").put("data", data).toString())
            h.postDelayed(this, 10000)
        }
    }

    override fun onBind(intent: Intent?) = null

    override fun onCreate() {
        super.onCreate()
        startForegroundNow()
        connect()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (ws == null) connect()
        return START_STICKY
    }

    override fun onDestroy() {
        h.removeCallbacksAndMessages(null)
        ws?.close(1000, "service stop")
        ws = null
        jobExecutor.shutdownNow()
        super.onDestroy()
    }

    private fun startForegroundNow() {
        val channel = "fleettridi"
        if (Build.VERSION.SDK_INT >= 26) {
            (getSystemService(NOTIFICATION_SERVICE) as NotificationManager)
                .createNotificationChannel(
                    NotificationChannel(channel, "FleetTridi Agent", NotificationManager.IMPORTANCE_LOW)
                )
        }

        val notification = if (Build.VERSION.SDK_INT >= 26)
            Notification.Builder(this, channel)
                .setContentTitle("FleetTridi conectado")
                .setContentText("Gerenciamento de frota ativo")
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .build()
        else
            Notification.Builder(this)
                .setContentTitle("FleetTridi conectado")
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .build()

        startForeground(73, notification)
    }

    private fun prefs() = getSharedPreferences("fleet", MODE_PRIVATE)

    private fun authRequest(url: String): Request.Builder {
        val p = prefs()
        return Request.Builder()
            .url(url)
            .header("X-Fleet-Device-Id", p.getString("deviceId", "") ?: "")
            .header("X-Fleet-Device-Token", p.getString("token", "") ?: "")
    }

    private fun connect() {
        val p = prefs()
        val server = (p.getString("server", "") ?: "").trimEnd('/')
        val did = p.getString("deviceId", "") ?: ""
        val token = p.getString("token", "") ?: ""

        if (server.isBlank() || did.isBlank() || token.isBlank()) {
            h.postDelayed({ if (ws == null) connect() }, 5000)
            return
        }

        val wsBase = when {
            server.startsWith("https://", true) -> "wss://" + server.substring(8)
            server.startsWith("http://", true) -> "ws://" + server.substring(7)
            else -> server
        }
        val url = wsBase + "/agent?deviceId=" + java.net.URLEncoder.encode(did, "UTF-8")

        try {
            ws = wsClient.newWebSocket(authRequest(url).build(), object: WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    ws = webSocket
                    hello(webSocket)
                    telemetryLoop()
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    jobExecutor.execute {
                        try { handle(text, webSocket) } catch (_: InterruptedException) { }
                    }
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    disconnectAndRetry(5000)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    disconnectAndRetry(3000)
                }
            })
        } catch (_: Exception) {
            ws = null
            h.postDelayed({ if (ws == null) connect() }, 5000)
        }
    }

    private fun disconnectAndRetry(delay: Long) {
        h.removeCallbacks(telemetryRunnable)
        ws = null
        h.postDelayed({ if (ws == null) connect() }, delay)
    }

    private fun hello(w: WebSocket) {
        val p = prefs()
        val root = privileged("id", 15)
        val pkg = p.getString("audiencePackage", "com.tridi.audience") ?: "com.tridi.audience"
        val privilegeMode = if (root.ok && root.output.contains("uid=0")) "su-root" else "unprivileged"
        val server = p.getString("server", "") ?: ""

        w.send(
            JSONObject()
                .put("type", "hello")
                .put("name", p.getString("name", Build.MODEL))
                .put("city", p.getString("city", ""))
                .put("site", p.getString("site", ""))
                .put("model", Build.MANUFACTURER + " " + Build.MODEL)
                .put("androidVersion", Build.VERSION.RELEASE)
                .put("agentVersion", BuildConfig.VERSION_NAME)
                .put("privilegeMode", privilegeMode)
                .put("rootAvailable", privilegeMode == "su-root")
                .put("serverUrl", server)
                .put("secureTransport", server.startsWith("https://", true))
                .put("audiencePackage", pkg)
                .put("audienceVersion", packageVersion(pkg))
                .toString()
        )
    }

    private fun telemetryLoop() {
        h.removeCallbacks(telemetryRunnable)
        h.post(telemetryRunnable)
    }

    private fun readText(path: String, max: Int = 512): String = try {
        File(path).inputStream().bufferedReader().use { it.readText().take(max) }
    } catch (_: Exception) {
        ""
    }

    private fun handle(text: String, w: WebSocket) {
        val root = JSONObject(text)
        if (root.optString("type") != "job") return

        val j = root.getJSONObject("job")
        val id = j.getString("id")
        val type = j.getString("type")
        val a = j.optJSONObject("args") ?: JSONObject()

        w.send(JSONObject().put("type", "jobAck").put("jobId", id).toString())

        try {
            when (type) {
                "installApk" -> {
                    val defaultPkg = prefs().getString("audiencePackage", "com.tridi.audience") ?: "com.tridi.audience"
                    val pkg = a.optString("packageName", defaultPkg).ifBlank { defaultPkg }
                    val expectedVersion = a.optString("expectedVersion", "").trim()
                    val restartAfterInstall = a.optString("restartAfterInstall", "true").toBoolean()
                    val result = installApk(
                        a.getString("url"),
                        a.optString("sha256", ""),
                        pkg,
                        expectedVersion,
                        restartAfterInstall
                    )
                    sendResult(w, id, result, if (result.ok) packageVersion(pkg) else null)
                }

                "syncCreative" ->
                    sendResult(w, id, syncCreative(a.getString("url"), a.getString("fileName"), a.optString("sha256", "")))

                "pushFile" ->
                    sendResult(w, id, pushFile(a.getString("url"), a.getString("target"), a.optString("sha256", "")))

                "restartAudience" -> sendResult(w, id, restartAudience())

                "rebootDevice" -> {
                    sendResult(w, id, OpResult(true, "reboot aceito; equipamento reiniciará em instantes"))
                    h.postDelayed({
                        if (!jobExecutor.isShutdown)
                            jobExecutor.execute { privileged("reboot", 30) }
                    }, 800)
                }

                "keyevent" -> sendResult(w, id, keyevent(a.getInt("key")))
                "tap" -> sendResult(w, id, tap(a.getInt("x"), a.getInt("y")))
                "swipe" -> sendResult(
                    w, id,
                    swipe(
                        a.getInt("x1"), a.getInt("y1"),
                        a.getInt("x2"), a.getInt("y2"),
                        a.optInt("ms", 300)
                    )
                )
                "captureScreen" -> captureScreen(w, id)
                else -> sendResult(w, id, OpResult(false, "operação não permitida pelo agente: $type"))
            }
        } catch (e: Exception) {
            sendResult(w, id, OpResult(false, e.message ?: "erro"))
        }
    }

    private fun installApk(
        url: String,
        expectedSha256: String,
        packageName: String,
        expectedVersion: String,
        restartAfterInstall: Boolean
    ): OpResult {
        if (!packageName.matches(Regex("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+")))
            return OpResult(false, "package inválido")

        val temp = File(cacheDir, "fleet_update.apk")
        val dl = download(url, temp, expectedSha256)
        if (!dl.ok) {
            temp.delete()
            return dl
        }

        val install = privileged("pm install -r -d " + quote(temp.absolutePath), 180)
        temp.delete()
        if (!install.ok) return install

        val installedVersion = packageVersion(packageName)
        if (installedVersion == "not-installed")
            return OpResult(false, "pm retornou sucesso, mas o package esperado não foi encontrado: $packageName")

        if (expectedVersion.isNotBlank() && installedVersion != expectedVersion)
            return OpResult(
                false,
                "versão instalada não confere. esperado=$expectedVersion recebido=$installedVersion"
            )

        prefs().edit().putString("audiencePackage", packageName).apply()

        var restartText = ""
        if (restartAfterInstall) {
            val restart = privileged("am force-stop $packageName; monkey -p $packageName 1", 30)
            restartText = if (restart.ok) "; app reiniciado" else "; APK instalado, mas reinício falhou: " + restart.output.take(500)
        }

        return OpResult(true, "APK instalado. package=$packageName version=$installedVersion$restartText")
    }

    private fun syncCreative(url: String, fileName: String, sha256: String): OpResult {
        val safe = File(fileName).name
        if (safe.isBlank()) return OpResult(false, "nome inválido")
        return downloadAndCopy(url, "/sdcard/TridiAudience/creatives/$safe", sha256)
    }

    private fun pushFile(url: String, target: String, sha256: String): OpResult {
        val normalized = try {
            File(target).canonicalPath
        } catch (_: Exception) {
            return OpResult(false, "caminho inválido")
        }

        val allowed = normalized.startsWith("/storage/emulated/0/TridiAudience/") ||
            normalized.startsWith("/sdcard/TridiAudience/") ||
            normalized.startsWith("/data/local/tmp/FleetTridi/")

        if (!allowed) return OpResult(false, "destino fora das áreas FleetTridi permitidas")
        return downloadAndCopy(url, normalized, sha256)
    }

    private fun downloadAndCopy(url: String, target: String, expectedSha256: String): OpResult {
        val temp = File(cacheDir, "fleet_file_" + System.nanoTime())
        val dl = download(url, temp, expectedSha256)
        if (!dl.ok) {
            temp.delete()
            return dl
        }

        val parent = File(target).parent ?: return OpResult(false, "destino inválido")
        val copy = privileged(
            "mkdir -p " + quote(parent) +
                " && cp " + quote(temp.absolutePath) + " " + quote(target) +
                " && chmod 644 " + quote(target),
            120
        )
        temp.delete()
        return copy
    }

    private fun download(url: String, target: File, expectedSha256: String): OpResult {
        return try {
            downloadClient.newCall(authRequest(url).build()).execute().use { response ->
                if (!response.isSuccessful) return OpResult(false, "HTTP " + response.code)
                val body = response.body ?: return OpResult(false, "resposta HTTP sem corpo")

                body.byteStream().use { input ->
                    target.outputStream().use { output -> input.copyTo(output) }
                }

                if (expectedSha256.isNotBlank()) {
                    val actual = sha256(target)
                    if (!actual.equals(expectedSha256, ignoreCase = true))
                        return OpResult(false, "SHA-256 inválido. esperado=$expectedSha256 recebido=$actual")
                }

                OpResult(true, "download verificado")
            }
        } catch (e: Exception) {
            OpResult(false, "falha no download: " + (e.message ?: e.javaClass.simpleName))
        }
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buffer)
                if (n <= 0) break
                digest.update(buffer, 0, n)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    private fun packageVersion(pkg: String): String = try {
        @Suppress("DEPRECATION")
        packageManager.getPackageInfo(pkg, 0).versionName ?: "unknown"
    } catch (_: Exception) {
        "not-installed"
    }

    private fun restartAudience(): OpResult {
        val pkg = prefs().getString("audiencePackage", "com.tridi.audience") ?: "com.tridi.audience"
        if (!pkg.matches(Regex("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z][A-Za-z0-9_]*)+")))
            return OpResult(false, "package inválido")
        return privileged("am force-stop $pkg; monkey -p $pkg 1", 30)
    }

    private fun keyevent(key: Int): OpResult {
        if (key !in 0..300) return OpResult(false, "keycode inválido")
        return privileged("input keyevent $key", 15)
    }

    private fun tap(x: Int, y: Int): OpResult {
        if (x < 0 || y < 0 || x > 10000 || y > 10000)
            return OpResult(false, "coordenadas inválidas")
        return privileged("input tap $x $y", 15)
    }

    private fun swipe(x1: Int, y1: Int, x2: Int, y2: Int, ms: Int): OpResult {
        if (listOf(x1, y1, x2, y2).any { it < 0 || it > 10000 })
            return OpResult(false, "coordenadas inválidas")
        val duration = ms.coerceIn(50, 5000)
        return privileged("input swipe $x1 $y1 $x2 $y2 $duration", 20)
    }

    private fun captureScreen(w: WebSocket, jobId: String) {
        val f = File(cacheDir, "screen.png")
        val result = privileged(
            "screencap -p " + quote(f.absolutePath) + " && chmod 644 " + quote(f.absolutePath),
            45
        )
        if (!result.ok || !f.exists()) {
            sendResult(w, jobId, result)
            return
        }

        try {
            val b64 = Base64.encodeToString(f.readBytes(), Base64.NO_WRAP)
            w.send(
                JSONObject()
                    .put("type", "jobResult")
                    .put("jobId", jobId)
                    .put("ok", true)
                    .put("output", "screenshot")
                    .put("binaryBase64", b64)
                    .toString()
            )
        } finally {
            f.delete()
        }
    }

    private fun sendResult(w: WebSocket, id: String, result: OpResult, audienceVersion: String? = null) {
        val msg = JSONObject()
            .put("type", "jobResult")
            .put("jobId", id)
            .put("ok", result.ok)
            .put("output", result.output.take(12000))

        if (!audienceVersion.isNullOrBlank())
            msg.put("audienceVersion", audienceVersion)

        w.send(msg.toString())
    }

    private fun shell(command: String, timeoutSeconds: Long = 30): OpResult =
        runProcess(listOf("sh", "-c", command), timeoutSeconds)

    private fun privileged(command: String, timeoutSeconds: Long = 120): OpResult =
        runProcess(listOf("su", "-c", command), timeoutSeconds)

    private fun runProcess(command: List<String>, timeoutSeconds: Long): OpResult {
        return try {
            val process = ProcessBuilder(command).redirectErrorStream(true).start()
            val output = StringBuilder()

            val reader = Thread {
                try {
                    process.inputStream.bufferedReader().useLines { lines ->
                        lines.forEach { line ->
                            if (output.length < 16000)
                                output.append(line).append('\n')
                        }
                    }
                } catch (_: Exception) { }
            }
            reader.isDaemon = true
            reader.start()

            val finished = process.waitFor(timeoutSeconds, TimeUnit.SECONDS)
            if (!finished) {
                process.destroy()
                if (!process.waitFor(2, TimeUnit.SECONDS))
                    process.destroyForcibly()
                reader.join(1000)
                return OpResult(false, "comando excedeu ${timeoutSeconds}s")
            }

            reader.join(1500)
            OpResult(process.exitValue() == 0, output.toString().trim())
        } catch (e: Exception) {
            OpResult(false, e.message ?: "comando indisponível")
        }
    }

    private fun quote(s: String) = "'" + s.replace("'", "'\\''") + "'"

    data class OpResult(val ok: Boolean, val output: String)
}
