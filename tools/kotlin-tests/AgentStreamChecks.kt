package net.oxog.phonebox.deskpanel

import java.io.ByteArrayInputStream
import java.io.InputStream
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

private fun CountDownLatch.reached() = check(await(5, TimeUnit.SECONDS)) { "Gate was not reached" }
private fun CountDownLatch.uninterruptible() {
    while (true) try { reached(); return } catch (_: InterruptedException) { }
}
private fun worker(stream: AgentStream): Thread =
    AgentStream::class.java.getDeclaredField("thread").apply { isAccessible = true }.get(stream) as Thread
private fun Thread.ended() { join(5000); check(!isAlive) { "Stream worker did not end" } }

private fun naturalCloseControl() {
    val closed = CountDownLatch(1)
    val calls = AtomicInteger()
    val stream = AgentStream(AgentLink {
        if (calls.getAndIncrement() > 0) throw java.io.IOException("offline fixture")
        ByteArrayInputStream(byteArrayOf())
    }) { event, _ -> if (event == "close") closed.countDown() }
    stream.start()
    val thread = worker(stream)
    closed.reached()
    stream.stop()
    thread.ended()
    println("CONTROL: current stream still delivers its natural close")
}

private fun lateClose(): List<String> {
    val closing = CountDownLatch(1)
    val releaseOld = CountDownLatch(1)
    val newOpen = CountDownLatch(1)
    val releaseNew = CountDownLatch(1)
    val calls = AtomicInteger()
    val events = Collections.synchronizedList(mutableListOf<String>())
    val old = object : InputStream() {
        override fun read() = -1
        override fun close() {
            if (Thread.currentThread().name == "agent-stream") {
                closing.countDown()
                releaseOld.uninterruptible()
            }
        }
    }
    val fresh = object : InputStream() {
        override fun read(): Int { releaseNew.uninterruptible(); return -1 }
        override fun close() { releaseNew.countDown() }
    }
    val stream = AgentStream(AgentLink { if (calls.getAndIncrement() == 0) old else fresh }) { event, _ ->
        events.add(event)
        if (event == "open" && events.count { it == "open" } == 2) newOpen.countDown()
    }
    stream.start()
    val oldThread = worker(stream)
    closing.reached() // old stream is suspended during cleanup
    stream.stop()
    stream.start()
    val newThread = worker(stream)
    newOpen.reached()
    releaseOld.countDown() // finish old cleanup strictly after the replacement opened
    oldThread.ended()
    val result = synchronized(events) { events.toList() }
    stream.stop()
    newThread.ended()
    return result
}

private fun lateData(): List<String> {
    val reading = CountDownLatch(1)
    val release = CountDownLatch(1)
    val events = Collections.synchronizedList(mutableListOf<String>())
    val first = "event: notify\ndata: stale\n".toByteArray()
    var sent = false
    val body = object : InputStream() {
        override fun read(): Int = error("Bulk reads only")
        override fun read(bytes: ByteArray, offset: Int, length: Int): Int {
            if (!sent) { sent = true; first.copyInto(bytes, offset); return first.size }
            reading.countDown()
            release.uninterruptible()
            bytes[offset] = 10
            return 1
        }
        override fun close() { } // represents a read completing concurrently with cancellation
    }
    val stream = AgentStream(AgentLink { body }) { event, _ -> events.add(event) }
    stream.start()
    val thread = worker(stream)
    reading.reached()
    stream.stop()
    release.countDown() // return the event terminator strictly after stop returned
    thread.ended()
    return synchronized(events) { events.toList() }
}

/**
 * A worker that passed its generation check must not deliver after stop() returned: stop() has to wait for a delivery
 * in flight. The "open" callback stalls here (as a descheduled worker would); stop() runs on another thread.
 * Returns whether stop() was still waiting while the delivery was stalled, and the events delivered after it returned.
 */
private fun stopDuringDelivery(): Pair<Boolean, List<String>> {
    val inDelivery = CountDownLatch(1)
    val release = CountDownLatch(1)
    val stopReturned = AtomicBoolean()
    val late = Collections.synchronizedList(mutableListOf<String>())
    val calls = AtomicInteger()
    val stream = AgentStream(AgentLink {
        if (calls.getAndIncrement() > 0) throw java.io.IOException("offline fixture")
        ByteArrayInputStream(byteArrayOf())
    }) { event, _ ->
        if (stopReturned.get()) late.add(event)
        if (event == "open") { inDelivery.countDown(); release.await(5, TimeUnit.SECONDS) }
    }
    stream.start()
    val thread = worker(stream)
    inDelivery.reached()
    val stopper = Thread { stream.stop(); stopReturned.set(true) }.apply { isDaemon = true; start() }
    val waited = try {
        stopper.join(300)
        stopper.isAlive
    } finally {
        release.countDown()
    }
    stopper.join(5000)
    check(!stopper.isAlive) { "stop() did not return" }
    thread.ended()
    return waited to synchronized(late) { late.toList() }
}

private fun closesBodyBeforeNotifying(failOn: String?): Boolean {
    val result = AtomicBoolean()
    val notified = CountDownLatch(1)
    val closed = AtomicBoolean()
    val calls = AtomicInteger()
    val stream = AgentStream(AgentLink {
        check(calls.getAndIncrement() == 0) { "Only the first response is needed" }
        object : ByteArrayInputStream("event: notify\ndata: hello\n\n".toByteArray()) {
            override fun close() { closed.set(true) }
        }
    }) { event, _ ->
        if (event == failOn) throw IllegalStateException("Injected delivery failure")
        if (event == "close") {
            result.set(closed.get())
            notified.countDown()
        }
    }
    stream.start()
    val thread = worker(stream)
    try {
        notified.reached()
        return result.get()
    } finally {
        stream.stop()
        thread.ended()
    }
}

private fun openDeliveryCleanup(mode: String?) {
    check(closesBodyBeforeNotifying(null)) { "A clean response must close its body" }
    println("CONTROL: successful delivery closed the response body")
    val closed = closesBodyBeforeNotifying("open")
    println("EXPECTED: response closed before close callback | ACTUAL: response closed=$closed")
    if (mode == "prove-open-cleanup") {
        println(if (closed) "PROBLEM NOT REPRODUCED" else "PROBLEM CONFIRMED")
        if (!closed) kotlin.system.exitProcess(1)
        return
    }
    check(closed) { "Open callback failure leaked the response body" }
    check(closesBodyBeforeNotifying("notify")) { "Data callback failure leaked the response body" }
    check(closesBodyBeforeNotifying("open")) { "Repeated open callback failure leaked the response body" }
    println("FIX VERIFIED")
}

fun main(args: Array<String>) {
    val mode = args.firstOrNull()
    if (mode == "prove-open-cleanup" || mode == "verify-open-cleanup") {
        openDeliveryCleanup(mode)
        return
    }
    naturalCloseControl()
    val closed = lateClose()
    println("EXPECTED: [open, open] | ACTUAL: $closed")
    if (mode == "prove") {
        val data = lateData()
        println("EXPECTED: no notify after stop | ACTUAL: $data")
        val ok = closed == listOf("open", "open") && "notify" !in data
        println(if (ok) "PROBLEM NOT REPRODUCED" else "PROBLEM CONFIRMED")
        if (!ok) kotlin.system.exitProcess(1)
        return
    }
    check(closed == listOf("open", "open")) { "Old generation closed the replacement" }
    val data = lateData()
    println("EXPECTED: no notify or close after stop | ACTUAL: $data")
    check("notify" !in data && "close" !in data) { "Stopped stream published post-stop event: $data" }
    val (waited, late) = stopDuringDelivery()
    println("EXPECTED: stop() waits for an in-flight delivery, nothing after it | ACTUAL: waited=$waited, after stop=$late")
    check(waited) { "stop() returned while a delivery was in flight" }
    check(late.isEmpty()) { "Event delivered after stop() returned: $late" }
    naturalCloseControl() // repeated start/stop cleanup still completes
    openDeliveryCleanup(mode)
}
