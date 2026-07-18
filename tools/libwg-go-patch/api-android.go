/* SPDX-License-Identifier: Apache-2.0
 *
 * Copyright © 2017-2022 Jason A. Donenfeld <Jason@zx2c4.com>. All Rights Reserved.
 */

package main

// #cgo LDFLAGS: -llog
// #include <android/log.h>
import "C"

import (
	"fmt"
	"math"
	"net"
	"os"
	"os/signal"
	"runtime"
	"runtime/debug"
	"strings"
	"sync"
	"unsafe"

	"golang.org/x/sys/unix"
	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/ipc"
	"golang.zx2c4.com/wireguard/tun"
)

type AndroidLogger struct {
	level C.int
	tag   *C.char
}

func cstring(s string) *C.char {
	b, err := unix.BytePtrFromString(s)
	if err != nil {
		b := [1]C.char{}
		return &b[0]
	}
	return (*C.char)(unsafe.Pointer(b))
}

func (l AndroidLogger) Printf(format string, args ...interface{}) {
	C.__android_log_write(l.level, l.tag, cstring(fmt.Sprintf(format, args...)))
}

type TunnelHandle struct {
	device *device.Device
	uapi   net.Listener
}

// SplitGuard patch: a tun.Device over a plain packet fd (an AF_UNIX SOCK_DGRAM
// socketpair end). VpnService apps that splice a userspace relay between the real
// tun and wireguard-go hand us one of these; TUNGETIFF is impossible on it (and
// SELinux denies the attempt), so name and MTU are fixed instead of queried.
type dgramTun struct {
	file   *os.File
	events chan tun.Event
	mtu    int
}

func (t *dgramTun) File() *os.File { return t.file }

func (t *dgramTun) Read(buf []byte, offset int) (int, error) {
	return t.file.Read(buf[offset:])
}

func (t *dgramTun) Write(buf []byte, offset int) (int, error) {
	return t.file.Write(buf[offset:])
}

func (t *dgramTun) Flush() error           { return nil }
func (t *dgramTun) MTU() (int, error)      { return t.mtu, nil }
func (t *dgramTun) Name() (string, error)  { return "splitguard0", nil }
func (t *dgramTun) Events() <-chan tun.Event { return t.events }

func (t *dgramTun) Close() error {
	close(t.events)
	return t.file.Close()
}

func makeDgramTun(fd int) (tun.Device, error) {
	// The failed CreateUnmonitoredTUNFromFD already wrapped fd in an os.File whose
	// finalizer will close it — own a dup instead so a later GC can't kill our fd.
	nfd, err := unix.Dup(fd)
	if err != nil {
		return nil, err
	}
	if err := unix.SetNonblock(nfd, true); err != nil {
		unix.Close(nfd)
		return nil, err
	}
	return &dgramTun{
		file:   os.NewFile(uintptr(nfd), "/dev/tun"),
		events: make(chan tun.Event, 4),
		mtu:    1280,
	}, nil
}

// SplitGuard patch: SplitGuard polls wgGetConfig for stats from a background thread while
// connect/disconnect run on the service thread. Upstream assumes a single caller and leaves
// the handle map unguarded, so those concurrent map read+write would trip Go's map-race
// detector and fatally abort the whole process. Serialize all handle-map access.
var (
	handlesMu     sync.RWMutex
	tunnelHandles map[int32]TunnelHandle
)

func init() {
	tunnelHandles = make(map[int32]TunnelHandle)
	signals := make(chan os.Signal)
	signal.Notify(signals, unix.SIGUSR2)
	go func() {
		buf := make([]byte, os.Getpagesize())
		for {
			select {
			case <-signals:
				n := runtime.Stack(buf, true)
				if n == len(buf) {
					n--
				}
				buf[n] = 0
				C.__android_log_write(C.ANDROID_LOG_ERROR, cstring("WireGuard/GoBackend/Stacktrace"), (*C.char)(unsafe.Pointer(&buf[0])))
			}
		}
	}()
}

//export wgTurnOn
func wgTurnOn(interfaceName string, tunFd int32, settings string) int32 {
	tag := cstring("WireGuard/GoBackend/" + interfaceName)
	logger := &device.Logger{
		Verbosef: AndroidLogger{level: C.ANDROID_LOG_DEBUG, tag: tag}.Printf,
		Errorf:   AndroidLogger{level: C.ANDROID_LOG_ERROR, tag: tag}.Printf,
	}

	tun, name, err := tun.CreateUnmonitoredTUNFromFD(int(tunFd))
	if err != nil {
		// SplitGuard patch: not a real tun fd (userspace relay socketpair) — wrap it
		// as a fixed-MTU packet device instead of failing.
		logger.Verbosef("CreateUnmonitoredTUNFromFD failed (%v); using dgram tun", err)
		dtun, derr := makeDgramTun(int(tunFd))
		if derr != nil {
			unix.Close(int(tunFd))
			logger.Errorf("CreateUnmonitoredTUNFromFD: %v; dgram fallback: %v", err, derr)
			return -1
		}
		tun, name = dtun, "splitguard0"
	}

	logger.Verbosef("Attaching to interface %v", name)
	device := device.NewDevice(tun, conn.NewStdNetBind(), logger)

	err = device.IpcSet(settings)
	if err != nil {
		// SplitGuard patch: close the tun.Device we actually built, not the raw tunFd.
		// On the dgram path tunFd is already owned by the os.File that the failed
		// CreateUnmonitoredTUNFromFD wrapped, so a raw unix.Close(tunFd) here would be a
		// double-close of that fd number (risking closing an unrelated reused fd).
		// device.Close() closes the native tun (real fd) or the dgram dup exactly once.
		device.Close()
		logger.Errorf("IpcSet: %v", err)
		return -1
	}
	device.DisableSomeRoamingForBrokenMobileSemantics()

	var uapi net.Listener

	uapiFile, err := ipc.UAPIOpen(name)
	if err != nil {
		logger.Errorf("UAPIOpen: %v", err)
	} else {
		uapi, err = ipc.UAPIListen(name, uapiFile)
		if err != nil {
			uapiFile.Close()
			logger.Errorf("UAPIListen: %v", err)
		} else {
			go func() {
				for {
					conn, err := uapi.Accept()
					if err != nil {
						return
					}
					go device.IpcHandle(conn)
				}
			}()
		}
	}

	err = device.Up()
	if err != nil {
		logger.Errorf("Unable to bring up device: %v", err)
		uapiFile.Close()
		device.Close()
		return -1
	}
	logger.Verbosef("Device started")

	handlesMu.Lock()
	var i int32
	for i = 0; i < math.MaxInt32; i++ {
		if _, exists := tunnelHandles[i]; !exists {
			break
		}
	}
	if i == math.MaxInt32 {
		handlesMu.Unlock()
		logger.Errorf("Unable to find empty handle")
		uapiFile.Close()
		device.Close()
		return -1
	}
	tunnelHandles[i] = TunnelHandle{device: device, uapi: uapi}
	handlesMu.Unlock()
	return i
}

//export wgTurnOff
func wgTurnOff(tunnelHandle int32) {
	handlesMu.Lock()
	handle, ok := tunnelHandles[tunnelHandle]
	if ok {
		delete(tunnelHandles, tunnelHandle)
	}
	handlesMu.Unlock()
	if !ok {
		return
	}
	if handle.uapi != nil {
		handle.uapi.Close()
	}
	handle.device.Close()
}

func lookupHandle(tunnelHandle int32) (TunnelHandle, bool) {
	handlesMu.RLock()
	handle, ok := tunnelHandles[tunnelHandle]
	handlesMu.RUnlock()
	return handle, ok
}

//export wgGetSocketV4
func wgGetSocketV4(tunnelHandle int32) int32 {
	handle, ok := lookupHandle(tunnelHandle)
	if !ok {
		return -1
	}
	bind, _ := handle.device.Bind().(conn.PeekLookAtSocketFd)
	if bind == nil {
		return -1
	}
	fd, err := bind.PeekLookAtSocketFd4()
	if err != nil {
		return -1
	}
	return int32(fd)
}

//export wgGetSocketV6
func wgGetSocketV6(tunnelHandle int32) int32 {
	handle, ok := lookupHandle(tunnelHandle)
	if !ok {
		return -1
	}
	bind, _ := handle.device.Bind().(conn.PeekLookAtSocketFd)
	if bind == nil {
		return -1
	}
	fd, err := bind.PeekLookAtSocketFd6()
	if err != nil {
		return -1
	}
	return int32(fd)
}

//export wgGetConfig
func wgGetConfig(tunnelHandle int32) *C.char {
	handle, ok := lookupHandle(tunnelHandle)
	if !ok {
		return nil
	}
	settings, err := handle.device.IpcGet()
	if err != nil {
		return nil
	}
	return C.CString(settings)
}

//export wgVersion
func wgVersion() *C.char {
	info, ok := debug.ReadBuildInfo()
	if !ok {
		return C.CString("unknown")
	}
	for _, dep := range info.Deps {
		if dep.Path == "golang.zx2c4.com/wireguard" {
			parts := strings.Split(dep.Version, "-")
			if len(parts) == 3 && len(parts[2]) == 12 {
				return C.CString(parts[2][:7])
			}
			return C.CString(dep.Version)
		}
	}
	return C.CString("unknown")
}

func main() {}
