package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"log"
	"os"
	"path/filepath"
	"sync"
	"unsafe"
)

var quickGDLL = struct {
	sync.Mutex
	cancel    context.CancelFunc
	running   bool
	lastError string
}{}

func cstr(value *C.char) string {
	if value == nil {
		return ""
	}
	return C.GoString(value)
}

func setDLLLastError(value string) {
	quickGDLL.Lock()
	quickGDLL.lastError = value
	quickGDLL.Unlock()
}

//export QuickGStart
func QuickGStart(listenPort C.int, controlPort C.int, brainPort C.int, regionPortStart C.int, regionPortEnd C.int, brainLeaseSeconds C.int, certPath *C.char, keyPath *C.char, alpn *C.char, brainBind *C.char, regionPortExclude *C.char) C.int {
	quickGDLL.Lock()
	if quickGDLL.running {
		quickGDLL.lastError = "Quick-G is already running"
		quickGDLL.Unlock()
		return 0
	}
	// Redirect Go log output to a file so in-process (DLL) runs are observable.
	if wd, err := os.Getwd(); err == nil {
		logPath := filepath.Join(wd, "Quick-G-dll.log")
		if f, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0644); err == nil {
			log.SetOutput(f)
		}
	}
	log.Printf("Quick-G DLL QuickGStart listen=%d control=%d brain=%d pool=%d-%d lease=%ds alpn=%s", int(listenPort), int(controlPort), int(brainPort), int(regionPortStart), int(regionPortEnd), int(brainLeaseSeconds), cstr(alpn))
	ctx, cancel := context.WithCancel(context.Background())
	quickGDLL.cancel = cancel
	quickGDLL.running = true
	quickGDLL.lastError = ""
	quickGDLL.Unlock()

	cfg := quickGConfig{
		ParentPid:         0,
		ListenPort:        int(listenPort),
		ControlPort:       int(controlPort),
		BrainBind:         cstr(brainBind),
		BrainPort:         int(brainPort),
		RegionPortStart:   int(regionPortStart),
		RegionPortEnd:     int(regionPortEnd),
		RegionPortExclude: cstr(regionPortExclude),
		BrainLeaseSeconds: int(brainLeaseSeconds),
		CertPath:          cstr(certPath),
		KeyPath:           cstr(keyPath),
		ALPN:              cstr(alpn),
	}
	if cfg.BrainBind == "" {
		cfg.BrainBind = "127.0.0.1"
	}
	if cfg.ALPN == "" {
		cfg.ALPN = "opensim-ll/1"
	}

	go func() {
		err := runQuickG(ctx, cfg)
		quickGDLL.Lock()
		quickGDLL.running = false
		quickGDLL.cancel = nil
		if err != nil {
			quickGDLL.lastError = err.Error()
		}
		quickGDLL.Unlock()
	}()

	return 1
}

//export QuickGStop
func QuickGStop() C.int {
	quickGDLL.Lock()
	cancel := quickGDLL.cancel
	quickGDLL.Unlock()
	if cancel == nil {
		return 0
	}
	cancel()
	return 1
}

//export QuickGIsRunning
func QuickGIsRunning() C.int {
	quickGDLL.Lock()
	running := quickGDLL.running
	quickGDLL.Unlock()
	if running {
		return 1
	}
	return 0
}

//export QuickGLastError
func QuickGLastError() *C.char {
	quickGDLL.Lock()
	value := quickGDLL.lastError
	quickGDLL.Unlock()
	return C.CString(value)
}

//export QuickGFreeString
func QuickGFreeString(value *C.char) {
	C.free(unsafe.Pointer(value))
}
