package com.tridi.fleet.agent

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build

class ConfigReceiver: BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        if (intent?.action != "com.tridi.fleet.agent.CONFIG") return

        val p = context.getSharedPreferences("fleet", Context.MODE_PRIVATE)

        // Enrollment por broadcast e intencionalmente one-shot.
        // Depois de configurado, alterações devem ser feitas localmente no app
        // ou após limpar os dados/reinstalar o agente durante manutenção física.
        if (p.getBoolean("enrolled", false)) return

        val server = intent.getStringExtra("server")?.trimEnd('/') ?: ""
        val deviceId = intent.getStringExtra("deviceId") ?: ""
        val token = intent.getStringExtra("token") ?: ""

        if (server.isBlank() || deviceId.isBlank() || token.length < 32) return

        p.edit()
            .putString("server", server)
            .putString("deviceId", deviceId)
            .putString("token", token)
            .putString("name", intent.getStringExtra("name") ?: Build.MODEL)
            .putString("city", intent.getStringExtra("city") ?: "")
            .putString("site", intent.getStringExtra("site") ?: "")
            .putString("audiencePackage", intent.getStringExtra("audiencePackage") ?: "com.tridi.audience")
            .putBoolean("enrolled", true)
            .apply()

        context.stopService(Intent(context, FleetService::class.java))
        if (Build.VERSION.SDK_INT >= 26)
            context.startForegroundService(Intent(context, FleetService::class.java))
        else
            context.startService(Intent(context, FleetService::class.java))
    }
}
