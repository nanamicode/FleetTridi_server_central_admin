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
            Notification.Builder(this,channel).setContentTitle("FleetTridi conectado").setContentText("Gerenciamento remoto ativo").setSmallIcon(android.R.drawable.stat_notify_sync).build()
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
        val req=Request.Builder().url(url).build()
        ws=client.newWebSocket(req,object:WebSocketListener(){
            override fun onOpen(webSocket:WebSocket,response:Response){ hello(webSocket); telemetryLoop() }
            override fun onMessage(webSocket:WebSocket,text:String){ Thread { handle(text,webSocket) }.start() }
            override fun onFailure(webSocket:WebSocket,t:Throwable,response:Response?){ ws=null; h.postDelayed({connect()},5000) }
            override fun onClosed(webSocket:WebSocket,code:Int,reason:String){ ws=null; h.postDelayed({connect()},3000) }
        })
    }

    private fun hello(w:WebSocket){
        val p=getSharedPreferences("fleet",MODE_PRIVATE)
        val o=JSONObject().put("type","hello").put("name",p.getString("name",Build.MODEL)).put("city",p.getString("city",""))
            .put("site",p.getString("site","")).put("model",Build.MANUFACTURER+" "+Build.MODEL)
            .put("androidVersion",Build.VERSION.RELEASE).put("agentVersion","0.1.0")
        w.send(o.toString())
    }

    private fun telemetryLoop(){
        val r=object:Runnable{ override fun run(){
            val mem=Root.exec("cat /proc/meminfo | head -5").output
            val temp=Root.exec("cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null").output.trim()
            val uptime=Root.exec("cat /proc/uptime").output.trim()
            val storage=Root.exec("df /data | tail -1").output.trim()
            val data=JSONObject().put("temperatureRaw",temp).put("uptime",uptime).put("memory",mem).put("storage",storage)
            ws?.send(JSONObject().put("type","telemetry").put("data",data).toString())
            h.postDelayed(this,10000)
        }}
        h.post(r)
    }

    private fun handle(text:String,w:WebSocket){
        val root=JSONObject(text)
        if(root.optString("type")!="job")return
        val j=root.getJSONObject("job")
        val id=j.getString("id")
        val type=j.getString("type")
        val a=j.optJSONObject("args")?:JSONObject()
        val result=try{
            when(type){
                "shell" -> Root.exec(a.optString("command"))
                "keyevent" -> Root.exec("input keyevent "+a.optString("key"))
                "tap" -> Root.exec("input tap "+a.optString("x")+" "+a.optString("y"))
                "swipe" -> Root.exec("input swipe "+a.optString("x1")+" "+a.optString("y1")+" "+a.optString("x2")+" "+a.optString("y2")+" "+a.optString("duration","250"))
                "screenshot" -> screenshot()
                "pushFile" -> downloadTo(a.getString("url"),a.getString("target"))
                "installApk" -> installApk(a.getString("url"))
                else -> Root.Result(false,"job desconhecido: "+type)
            }
        }catch(e:Exception){ Root.Result(false,e.stackTraceToString()) }
        w.send(JSONObject().put("type","jobResult").put("jobId",id).put("ok",result.ok).put("output",result.output).toString())
    }

    private fun screenshot():Root.Result{
        val f=File(cacheDir,"screen.png")
        val r=Root.exec("screencap -p "+Root.q(f.absolutePath))
        if(!r.ok||!f.exists())return Root.Result(false,r.output)
        return Root.Result(true,android.util.Base64.encodeToString(f.readBytes(),android.util.Base64.NO_WRAP))
    }

    private fun downloadTo(url:String,target:String):Root.Result{
        val temp=File(cacheDir,"download_"+System.nanoTime())
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return Root.Result(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        val parent=File(target).parent ?: "/sdcard"
        return Root.exec("mkdir -p "+Root.q(parent)+" && cp "+Root.q(temp.absolutePath)+" "+Root.q(target)+" && chmod 644 "+Root.q(target))
    }

    private fun installApk(url:String):Root.Result{
        val temp=File(cacheDir,"fleet_update.apk")
        val response=client.newCall(Request.Builder().url(url).build()).execute()
        if(!response.isSuccessful)return Root.Result(false,"HTTP "+response.code)
        response.body!!.byteStream().use { input->temp.outputStream().use{output->input.copyTo(output)}}
        return Root.exec("pm install -r -d "+Root.q(temp.absolutePath))
    }
}