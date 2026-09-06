package com.tridi.fleet.agent
object Root {
    data class Result(val ok:Boolean,val output:String)
    fun exec(command:String):Result = try {
        val p=ProcessBuilder("su","-c",command).redirectErrorStream(true).start()
        val out=p.inputStream.bufferedReader().readText()
        val code=p.waitFor()
        Result(code==0,out)
    } catch(e:Exception){ Result(false,e.stackTraceToString()) }
    fun q(s:String)="'"+s.replace("'","'\\''")+"'"
}