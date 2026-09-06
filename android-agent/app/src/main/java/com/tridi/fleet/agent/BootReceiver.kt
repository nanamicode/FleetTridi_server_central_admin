package com.tridi.fleet.agent
import android.content.*
class BootReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if(intent?.action == Intent.ACTION_BOOT_COMPLETED) context.startForegroundService(Intent(context,FleetService::class.java))
    }
}