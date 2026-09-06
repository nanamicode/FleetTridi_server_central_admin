package com.tridi.fleet.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class ConfigReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if(intent?.action != "com.tridi.fleet.agent.CONFIG") return
        val p=context.getSharedPreferences("fleet",Context.MODE_PRIVATE)
        p.edit()
            .putString("server",intent.getStringExtra("server")?.trimEnd('/') ?: "")
            .putString("deviceId",intent.getStringExtra("deviceId") ?: "")
            .putString("token",intent.getStringExtra("token") ?: "")
            .putString("name",intent.getStringExtra("name") ?: android.os.Build.MODEL)
            .putString("city",intent.getStringExtra("city") ?: "")
            .putString("site",intent.getStringExtra("site") ?: "")
            .apply()
        context.stopService(Intent(context,FleetService::class.java))
        context.startForegroundService(Intent(context,FleetService::class.java))
    }
}