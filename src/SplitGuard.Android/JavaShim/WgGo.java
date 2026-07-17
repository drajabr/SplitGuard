package com.wireguard.android.backend;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;

/**
 * Thin gateway to the wireguard-android tunnel library's embedded wireguard-go
 * (libwg-go.so). GoBackend declares the JNI entry points as private static native
 * methods and offers no hook to customize the VpnService.Builder (which SplitGuard
 * needs for split DNS), so we own the VpnService in C# and call the natives directly.
 * Same-package reflection because the methods are private; the JNI symbols themselves
 * are bound to the GoBackend class name, so they cannot be redeclared elsewhere.
 */
public final class WgGo {
    static {
        System.loadLibrary("wg-go");
    }

    private static Method method(String name, Class<?>... params) {
        try {
            Method m = GoBackend.class.getDeclaredMethod(name, params);
            m.setAccessible(true);
            return m;
        } catch (NoSuchMethodException e) {
            throw new IllegalStateException("wireguard tunnel library changed: " + name, e);
        }
    }

    private static final Method TURN_ON = method("wgTurnOn", String.class, int.class, String.class);
    private static final Method TURN_OFF = method("wgTurnOff", int.class);
    private static final Method GET_CONFIG = method("wgGetConfig", int.class);
    private static final Method SOCKET_V4 = method("wgGetSocketV4", int.class);
    private static final Method SOCKET_V6 = method("wgGetSocketV6", int.class);
    private static final Method VERSION = method("wgVersion");

    private static Object call(Method m, Object... args) {
        try {
            return m.invoke(null, args);
        } catch (IllegalAccessException e) {
            throw new IllegalStateException(e);
        } catch (InvocationTargetException e) {
            Throwable c = e.getCause();
            throw c instanceof RuntimeException ? (RuntimeException) c : new IllegalStateException(c);
        }
    }

    private WgGo() {}

    /** Starts wireguard-go on the given tun fd; returns a tunnel handle (&lt; 0 = error). */
    public static int turnOn(String name, int tunFd, String uapiSettings) {
        return (Integer) call(TURN_ON, name, tunFd, uapiSettings);
    }

    public static void turnOff(int handle) {
        call(TURN_OFF, handle);
    }

    /** UAPI text: per-peer rx_bytes / tx_bytes / last_handshake_time_sec etc. */
    public static String getConfig(int handle) {
        return (String) call(GET_CONFIG, handle);
    }

    /** The encrypted-UDP socket fds; MUST be VpnService.protect()ed to avoid a routing loop. */
    public static int socketV4(int handle) {
        return (Integer) call(SOCKET_V4, handle);
    }

    public static int socketV6(int handle) {
        return (Integer) call(SOCKET_V6, handle);
    }

    public static String version() {
        return (String) call(VERSION);
    }
}
