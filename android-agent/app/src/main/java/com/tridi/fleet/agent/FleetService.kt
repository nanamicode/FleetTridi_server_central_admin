package com.tridi.fleet.agent

import android.app.*
import android.content.Intent
import android.os.*
import android.util.Base64
import okhttp3.*
import org.json.JSONObject
import java.io.File
import java.util.concurrent.TimeUnit

class FleetService: Service() {
    private val client=OkHttpClient.Builder().pingInterval(15,TimeUnit.SECONDS).readTimeout(0,TimeUnit.MILLISECONDS).build()
    private var ws:WebSocket?=null
    private val h=Handler(Looper.getMainLooper())

    private val telemetryRunnable=object:Runnable {
        override fun run() {
            if(ws==null)return
            val data=JSONObject()
                .put("temperatureRaw",readText("/sys/class/thermal/thermal_zone0/temp"))
                .put("uptime",readText("/proc/uptime"))
                .put("memory",readText("/proc/meminfo",4096))
                .put("storageFreeBytes",File("/data").usableSpace)
                .put("storageTotalBytes",File("/data").totalSpace)
                .put("cpuCores",Runtime.getRuntime().availableProcessors())
                .put("loadAverage",readText("/proc/loadavg"))
                .put("screen",shell("wm size").output)
                .put("timestamp",System.currentTimeMillis())
            ws?.send(JSONObject().put("type","telemetry").put("data",data).toString())
            h.postDelayed(this,10000)
        }
    }

    override fun onBind(intent: Intent?)=null
    override fun onCreate(){ super.onCreate(); startForegroundNow(); connect() }
    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int { if(ws==null) connect(); return START_STICKY }
    override fun onDestroy(){ h.removeCallbacks(telemetryRunnable); ws?.close(1000,"service stop"); ws=null; super.onDestroy() }

    private fun startForegroundNow(){
        val channel="fleettridi"
        if(Build.VERSION.SDK_INT>=26) {
            (getSystemService(NOTIFICATION_SERVICE) as NotificationManager)
                .createNotificationChannel(NotificationChannel(channel,"FleetTridi Agent",NotificationManager.IMPORTANCE_LOW))
        }
        val n=if(Build.VERSION.SDK_INT>=26)
            Notification.Builder(this,channel).setContentTitle("FleetTridi conectado").setContentText("Gerenciamento de frota ativo").setSmallIcon(android.R.drawable.stat_notify_sync).build()
        else Notification.Builder(this).setContentTitle("FleetTridi conectado").setSmallIcon(android.R.drawable.stat_notify_sync).build()
        startForeground(73,n)
    }

    private fun connect(){
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        val server=p.getString("server","")!!.trimEnd('/')
        val did=p.getString("deviceId","")!!
        val token=p.getString("token","")!!
        if(server.isBlank()||did.isBlank()||token.isBlank()){ h.postDelayed({connect()},5000); return }

        val wsBase=when {
            server.startsWith("https://")->"wss://"+server.removePrefix("https://")
            server.startsWith("http://")->"ws://"+server.removePrefix("http://")
            else->server
        }
        val url=wsBase+"/agent?deviceId="+java.net.URLEncoder.encode(did,"UTF-8")+"&token="+java.net.URLEncoder.encode(token,"UTF-8")
        try {
            ws=client.newWebSocket(Request.Builder().url(url).build(),object:WebSocketListener(){
                override fun onOpen(webSocket:WebSocket,response:Response){ hello(webSocket); telemetryLoop() }
                override fun onMessage(webSocket:WebSocket,text:String){ Thread { handle(text,webSocket) }.start() }
                override fun onFailure(webSocket:WebSocket,t:Throwable,response:Response?){ disconnectAndRetry(5000) }
                override fun onClosed(webSocket:WebSocket,code:Int,reason:String){ disconnectAndRetry(3000) }
            })
        } catch(_:Exception) { ws=null; h.postDelayed({connect()},5000) }
    }

    private fun disconnectAndRetry(delay:Long){
        h.removeCallbacks(telemetryRunnable)
        ws=null
        h.postDelayed({ if(ws==null) connect() },delay)
    }

    private fun hello(w:WebSocket){
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        val root=privileged("id")
        w.send(JSONObject()
            .put("type","hello")
            .put("name",p.getString("name",Build.MODEL))
            .put("city",p.getString("city",""))
            .put("site",p.getString("site",""))
            .put("model",Build.MANUFACTURER+" "+Build.MODEL)
            .put("androidVersion",Build.VERSION.RELEASE)
            .put("agentVersion","0.3.0")
            .put("rootAvailable",root.ok && root.output.contains("uid=0"))
            .toString())
    }

    private fun telemetryLoop(){
        h.removeCallbacks(telemetryRunnable)
        h.post(telemetryRunnable)
    }

    private fun readText(path:String,max:Int=512):String = try {
        File(path).inputStream().bufferedReader().use { it.readText().take(max) }
    } catch(_:Exception){ "" }

    private fun handle(text:String,w:WebSocket){
        val root=JSONObject(text)
        if(root.optString("type")!="job")return
        val j=root.getJSONObject("job")
        val id=j.getString("id")
        val type=j.getString("type")
        val a=j.optJSONObject("args")?:JSONObject()
        w.send(JSONObject().put("type","jobAck").put("jobId",id).toString())

        try {
            when(type){
                "installApk" -> sendResult(w,id,installApk(a.getString("url")))
                "syncCreative" -> sendResult(w,id,syncCreative(a.getString("url"),a.getString("fileName")))
                "pushFile" -> sendResult(w,id,pushFile(a.getString("url"),a.getString("target")))
                "restartAudience" -> sendResult(w,id,restartAudience())
                "rebootDevice" -> sendResult(w,id,privileged("reboot"))
                "keyevent" -> sendResult(w,id,keyevent(a.getInt("key")))
                "tap" -> sendResult(w,id,tap(a.getInt("x"),a.getInt("y")))
                "swipe" -> sendResult(w,id,swipe(a.getInt("x1"),a.getInt("y1"),a.getInt("x2"),a.getInt("y2"),a.optInt("ms",300)))
                "captureScreen" -> captureScreen(w,id)
                else -> sendResult(w,id,OpResult(false,"operação não permitida pelo agente: "+type))
            }
        }catch(e:Exception){ sendResult(w,id,OpResult(false,e.message ?: "erro")) }
    }

    private fun installApk(url:String):OpResult{
        val temp=File(cacheDir,"fleet_update.apk")
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return OpResult(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        val r=privileged("pm install -r -d "+quote(temp.absolutePath))
        temp.delete()
        return r
    }

    private fun syncCreative(url:String,fileName:String):OpResult{
        val safe=File(fileName).name
        if(safe.isBlank())return OpResult(false,"nome inválido")
        return downloadAndCopy(url,"/sdcard/TridiAudience/creatives/"+safe)
    }

    private fun pushFile(url:String,target:String):OpResult{
        val normalized=try { File(target).canonicalPath } catch(_:Exception){ return OpResult(false,"caminho inválido") }
        val allowed=normalized.startsWith("/storage/emulated/0/TridiAudience/") ||
            normalized.startsWith("/sdcard/TridiAudience/") ||
            normalized.startsWith("/data/local/tmp/FleetTridi/")
        if(!allowed)return OpResult(false,"destino fora das áreas FleetTridi permitidas")
        return downloadAndCopy(url,normalized)
    }

    private fun downloadAndCopy(url:String,target:String):OpResult{
        val temp=File(cacheDir,"fleet_file_"+System.nanoTime())
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return OpResult(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        val parent=File(target).parent ?: return OpResult(false,"destino inválido")
        val r=privileged("mkdir -p "+quote(parent)+" && cp "+quote(temp.absolutePath)+" "+quote(target)+" && chmod 644 "+quote(target))
        temp.delete()
        return r
    }

    private fun restartAudience():OpResult{
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        val pkg=p.getString("audiencePackage","com.tridi.audience") ?: "com.tridi.audience"
        if(!pkg.matches(Regex("[A-Za-z0-9_.]+")))return OpResult(false,"package inválido")
        return privileged("am force-stop "+pkg+"; monkey -p "+pkg+" 1")
    }

    private fun keyevent(key:Int):OpResult{
        if(key !in 0..300)return OpResult(false,"keycode inválido")
        return privileged("input keyevent "+key)
    }

    private fun tap(x:Int,y:Int):OpResult{
        if(x<0||y<0||x>10000||y>10000)return OpResult(false,"coordenadas inválidas")
        return privileged("input tap "+x+" "+y)
    }

    private fun swipe(x1:Int,y1:Int,x2:Int,y2:Int,ms:Int):OpResult{
        if(listOf(x1,y1,x2,y2).any{it<0||it>10000})return OpResult(false,"coordenadas inválidas")
        return privileged("input swipe "+x1+" "+y1+" "+x2+" "+y2+" "+ms.coerceIn(50,5000))
    }

    private fun captureScreen(w:WebSocket,jobId:String){
        val f=File(cacheDir,"screen.png")
        val r=privileged("screencap -p "+quote(f.absolutePath)+" && chmod 644 "+quote(f.absolutePath))
        if(!r.ok||!f.exists()){ sendResult(w,jobId,r); return }
        val b64=Base64.encodeToString(f.readBytes(),Base64.NO_WRAP)
        w.send(JSONObject().put("type","jobResult").put("jobId",jobId).put("ok",true).put("output","screenshot").put("binaryBase64",b64).toString())
    }

    private fun sendResult(w:WebSocket,id:String,r:OpResult){
        w.send(JSONObject().put("type","jobResult").put("jobId",id).put("ok",r.ok).put("output",r.output.take(12000)).toString())
    }

    private fun shell(command:String):OpResult = try {
        val p=ProcessBuilder("sh","-c",command).redirectErrorStream(true).start()
        val out=p.inputStream.bufferedReader().readText()
        OpResult(p.waitFor()==0,out)
    } catch(e:Exception){ OpResult(false,e.message ?: "shell indisponível") }

    private fun privileged(command:String):OpResult = try {
        val p=ProcessBuilder("su","-c",command).redirectErrorStream(true).start()
        val out=p.inputStream.bufferedReader().readText()
        OpResult(p.waitFor()==0,out)
    } catch(e:Exception){ OpResult(false,e.message ?: "root indisponível") }

    private fun quote(s:String)="'"+s.replace("'","'\\''")+"'"
    data class OpResult(val ok:Boolean,val output:String)
}