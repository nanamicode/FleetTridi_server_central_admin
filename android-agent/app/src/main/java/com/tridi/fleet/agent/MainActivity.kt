package com.tridi.fleet.agent

import android.app.Activity
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.widget.*

class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val p = getSharedPreferences("fleet", MODE_PRIVATE)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(28, 28, 28, 28)
        }

        val enrolled = p.getBoolean("enrolled", false)
        val title = TextView(this).apply {
            text = if (enrolled) "FleetTridi Agent — provisionado" else "FleetTridi Agent — configuração local"
            textSize = 20f
        }
        root.addView(title)

        if (enrolled) {
            val server = p.getString("server", "") ?: ""
            val deviceId = p.getString("deviceId", "") ?: ""
            val name = p.getString("name", Build.MODEL) ?: Build.MODEL
            val city = p.getString("city", "") ?: ""
            val site = p.getString("site", "") ?: ""
            val pkg = p.getString("audiencePackage", "com.tridi.audience") ?: "com.tridi.audience"

            root.addView(TextView(this).apply {
                text = "Nome: $name\nCidade: $city\nLocal: $site\nDevice ID: $deviceId\nServidor: $server\nTridiAudience: $pkg\nAgente: ${BuildConfig.VERSION_NAME}"
                setPadding(0, 18, 0, 18)
            })

            root.addView(TextView(this).apply {
                text = "As credenciais de enrollment ficam bloqueadas após o provisionamento para evitar alterações acidentais. Para reprovisionar, use o bootstrap ADB, que limpa apenas os dados do FleetTridi Agent."
            })

            val restart = Button(this).apply {
                text = "REINICIAR CONEXÃO DO AGENTE"
                setOnClickListener {
                    stopService(Intent(this@MainActivity, FleetService::class.java))
                    if (Build.VERSION.SDK_INT >= 26)
                        startForegroundService(Intent(this@MainActivity, FleetService::class.java))
                    else
                        startService(Intent(this@MainActivity, FleetService::class.java))
                    Toast.makeText(this@MainActivity, "Conexão reiniciada", Toast.LENGTH_SHORT).show()
                }
            }
            root.addView(restart)
        } else {
            fun field(hint: String, value: String, secret: Boolean = false): EditText = EditText(this).apply {
                this.hint = hint
                setText(value)
                if (secret) inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
                root.addView(this)
            }

            val server = field("Servidor central", p.getString("server", "") ?: "")
            val id = field("Device ID", p.getString("deviceId", "") ?: "")
            val token = field("Enrollment token", p.getString("token", "") ?: "", true)
            val name = field("Nome do totem", p.getString("name", Build.MODEL) ?: Build.MODEL)
            val city = field("Cidade", p.getString("city", "") ?: "")
            val site = field("Local/ponto", p.getString("site", "") ?: "")

            val save = Button(this).apply {
                text = "SALVAR E CONECTAR"
                setOnClickListener {
                    val serverValue = server.text.toString().trimEnd('/')
                    val idValue = id.text.toString().trim()
                    val tokenValue = token.text.toString().trim()

                    if (serverValue.isBlank() || idValue.isBlank() || tokenValue.length < 32) {
                        Toast.makeText(this@MainActivity, "Servidor, Device ID e token são obrigatórios.", Toast.LENGTH_LONG).show()
                        return@setOnClickListener
                    }

                    p.edit()
                        .putString("server", serverValue)
                        .putString("deviceId", idValue)
                        .putString("token", tokenValue)
                        .putString("name", name.text.toString())
                        .putString("city", city.text.toString())
                        .putString("site", site.text.toString())
                        .putBoolean("enrolled", true)
                        .apply()

                    if (Build.VERSION.SDK_INT >= 26)
                        startForegroundService(Intent(this@MainActivity, FleetService::class.java))
                    else
                        startService(Intent(this@MainActivity, FleetService::class.java))

                    Toast.makeText(this@MainActivity, "FleetTridi Agent iniciado", Toast.LENGTH_SHORT).show()
                    recreate()
                }
            }
            root.addView(save)
        }

        setContentView(root)
    }
}
