package com.tridi.fleet.agent

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.widget.*

class MainActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val p = getSharedPreferences("fleet", MODE_PRIVATE)
        val root = LinearLayout(this).apply { orientation=LinearLayout.VERTICAL; setPadding(28,28,28,28) }
        fun field(hint:String, value:String): EditText = EditText(this).apply { this.hint=hint; setText(value); root.addView(this) }
        val server=field("Servidor central",p.getString("server","http://10.0.2.2:8787")!!)
        val id=field("Device ID",p.getString("deviceId","")!!)
        val token=field("Enrollment token",p.getString("token","")!!)
        val name=field("Nome do totem",p.getString("name",android.os.Build.MODEL)!!)
        val city=field("Cidade",p.getString("city","")!!)
        val site=field("Local/ponto",p.getString("site","")!!)
        val save=Button(this).apply { text="SALVAR E CONECTAR"; setOnClickListener {
            p.edit().putString("server",server.text.toString().trimEnd('/')).putString("deviceId",id.text.toString())
                .putString("token",token.text.toString()).putString("name",name.text.toString())
                .putString("city",city.text.toString()).putString("site",site.text.toString()).apply()
            startForegroundService(Intent(this@MainActivity,FleetService::class.java))
            Toast.makeText(this@MainActivity,"FleetTridi Agent iniciado",Toast.LENGTH_SHORT).show()
        } }
        root.addView(save)
        setContentView(root)
    }
}