package com.tridi.fleet.agent

import android.app.*
import android.content.Intent
import android.os.*
import okhttp3.*
import org.json.JSONObject
import java.io.File
import java.util.concurrent.TimeUnit

class FleetService: Service() {
    private val client=OkHttpClient.Builder().pingInterval(15,TimeUnit.SECONDS).readTimeout(0,TimeUnit.MILLISECONDS).build()
    private var ws:WebSocket?=null
    private val h=Handler(Looper.getMainLooper())

    override fun onBind(intent: Intent?)=null
    override fun onCreate(){ super.onCreate(); startForegroundNow(); connect() }
    override fun onStartCommand(intent:Intent?,flags:Int,startId:Int):Int { if(ws==null) connect(); return START_STICKY }

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
        ws=client.newWebSocket(Request.Builder().url(url).build(),object:WebSocketListener(){
            override fun onOpen(webSocket:WebSocket,response:Response){ hello(webSocket); telemetryLoop() }
            override fun onMessage(webSocket:WebSocket,text:String){ Thread { handle(text,webSocket) }.start() }
            override fun onFailure(webSocket:WebSocket,t:Throwable,response:Response?){ ws=null; h.postDelayed({connect()},5000) }
            override fun onClosed(webSocket:WebSocket,code:Int,reason:String){ ws=null; h.postDelayed({connect()},3000) }
        })
    }

    private fun hello(w:WebSocket){
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        w.send(JSONObject()
            .put("type","hello")
            .put("name",p.getString("name",Build.MODEL))
            .put("city",p.getString("city",""))
            .put("site",p.getString("site",""))
            .put("model",Build.MANUFACTURER+" "+Build.MODEL)
            .put("androidVersion",Build.VERSION.RELEASE)
            .put("agentVersion","0.2.0").toString())
    }

    private fun telemetryLoop(){
        val r=object:Runnable{ override fun run(){
            val data=JSONObject()
                .put("temperatureRaw",readText("/sys/class/thermal/thermal_zone0/temp"))
                .put("uptime",readText("/proc/uptime"))
                .put("memory",readText("/proc/meminfo",4096))
                .put("storage",File("/data").usableSpace)
                .put("storageTotal",File("/data").totalSpace)
                .put("freeMemory",Runtime.getRuntime().freeMemory())
            ws?.send(JSONObject().put("type","telemetry").put("data",data).toString())
            h.postDelayed(this,10000)
        }}
        h.post(r)
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

        val result=try{
            when(type){
                "installApk" -> installApk(a.getString("url"))
                "syncCreative" -> syncCreative(a.getString("url"),a.getString("fileName"))
                "restartAudience" -> restartAudience()
                "rebootDevice" -> privileged("reboot")
                else -> OpResult(false,"operação não permitida pelo agente: "+type)
            }
        }catch(e:Exception){ OpResult(false,e.message ?: "erro") }

        w.send(JSONObject().put("type","jobResult").put("jobId",id).put("ok",result.ok).put("output",result.output).toString())
    }

    private fun installApk(url:String):OpResult{
        val temp=File(cacheDir,"fleet_update.apk")
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return OpResult(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        return privileged("pm install -r -d "+quote(temp.absolutePath))
    }

    private fun syncCreative(url:String,fileName:String):OpResult{
        val safe=File(fileName).name
        if(safe.isBlank())return OpResult(false,"nome inválido")
        val temp=File(cacheDir,"creative_"+System.nanoTime())
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return OpResult(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        val dir="/sdcard/TridiAudience/creatives"
        return privileged("mkdir -p "+quote(dir)+" && cp "+quote(temp.absolutePath)+" "+quote(dir+"/"+safe)+" && chmod 644 "+quote(dir+"/"+safe))
    }

    private fun restartAudience():OpResult{
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        val pkg=p.getString("audiencePackage","com.tridi.audience") ?: "com.tridi.audience"
        if(!pkg.matches(Regex("[A-Za-z0-9_.]+")))return OpResult(false,"package inválido")
        return privileged("am force-stop "+pkg+"; monkey -p "+pkg+" 1")
    }

    private fun privileged(command:String):OpResult = try {
        val p=ProcessBuilder("su","-c",command).redirectErrorStream(true).start()
        val out=p.inputStream.bufferedReader().readText()
        OpResult(p.waitFor()==0,out)
    } catch(e:Exception){ OpResult(false,e.message ?: "root indisponível") }

    private fun quote(s:String)="'"+s.replace("'","'\\''")+"'"
    data class OpResult(val ok:Boolean,val output:String)
}