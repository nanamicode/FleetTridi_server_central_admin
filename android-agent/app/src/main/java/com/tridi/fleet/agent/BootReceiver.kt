package com.tridi.fleet.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class BootReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if(intent?.action != Intent.ACTION_BOOT_COMPLETED) return
        val p=context.getSharedPreferences("fleet",Context.MODE_PRIVATE)
        if(!p.getString("server","").isNullOrBlank() && !p.getString("deviceId","").isNullOrBlank() && !p.getString("token","").isNullOrBlank()) {
            context.startForegroundService(Intent(context,FleetService::class.java))
        }
    }
}