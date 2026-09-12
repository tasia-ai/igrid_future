// Quick-G provides the quic-go based QUIC front end needed by Windows 10.
package main

import (
	"context"
	"crypto/tls"
	"encoding/binary"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"strconv"
	"sync"
	"time"

	quic "github.com/quic-go/quic-go"
)

type registration struct {
	CircuitCode uint32 `json:"circuitCode"`
	QuicHost    string `json:"quicHost"`
	SimHost     string `json:"simHost"`
	QuicPort    int    `json:"quicPort"`
	SimPort     int    `json:"simPort"`
}

var routes = struct {
	sync.RWMutex
	m map[uint32]string
}{m: make(map[uint32]string)}

func main() {
	parent := flag.Int("parent-pid", 0, "ROBUST process id")
	port := flag.Int("listen-port", 9001, "public QUIC port")
	control := flag.Int("control-port", 19001, "loopback registration port")
	cert := flag.String("cert", "SSL/quic/quic-cert.pem", "PEM certificate")
	key := flag.String("key", "SSL/quic/quic-key.pem", "PEM private key")
	alpn := flag.String("alpn", "opensim-ll/1", "QUIC ALPN")
	flag.Parse()
	if *parent <= 0 {
		log.Fatal("--parent-pid is required")
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go monitorParentPipe(ctx, *parent, cancel)

	pair, err := tls.LoadX509KeyPair(*cert, *key)
	if err != nil {
		log.Fatalf("load TLS key pair: %v", err)
	}
	listener, err := quic.ListenAddr(fmt.Sprintf(":%d", *port), &tls.Config{Certificates: []tls.Certificate{pair}, NextProtos: []string{*alpn}}, &quic.Config{KeepAlivePeriod: 20 * time.Second, MaxIdleTimeout: 60 * time.Second})
	if err != nil {
		log.Fatalf("listen: %v", err)
	}
	defer listener.Close()
	go serveControl(ctx, *control)
	go func() { <-ctx.Done(); listener.Close() }()
	log.Printf("Quick-G listening on UDP %d (parent %d)", *port, *parent)
	for {
		conn, err := listener.Accept(ctx)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			log.Printf("accept: %v", err)
			continue
		}
		go bridge(ctx, conn, *alpn)
	}
}

func serveControl(ctx context.Context, port int) {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"ready":true}`)
	})
	mux.HandleFunc("/register", func(w http.ResponseWriter, r *http.Request) {
		var v registration
		if r.Method != http.MethodPost || json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&v) != nil || v.CircuitCode == 0 {
			http.Error(w, "bad registration", 400)
			return
		}
		host, port := v.QuicHost, v.QuicPort
		if host == "" {
			host = v.SimHost
		}
		if port <= 0 && v.SimPort > 0 {
			port = v.SimPort + 7000
		}
		if port <= 0 || port > 65535 {
			http.Error(w, "bad endpoint", 400)
			return
		}
		routes.Lock()
		routes.m[v.CircuitCode] = net.JoinHostPort(host, strconv.Itoa(port))
		routes.Unlock()
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"success":true}`)
	})
	mux.HandleFunc("/unregister", func(w http.ResponseWriter, r *http.Request) {
		var v registration
		if json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&v) != nil {
			http.Error(w, "bad registration", 400)
			return
		}
		routes.Lock()
		delete(routes.m, v.CircuitCode)
		routes.Unlock()
		io.WriteString(w, `{"success":true}`)
	})
	s := &http.Server{Addr: net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), Handler: mux, ReadHeaderTimeout: 3 * time.Second}
	go func() {
		<-ctx.Done()
		shutdown, cancel := context.WithTimeout(context.Background(), time.Second)
		defer cancel()
		s.Shutdown(shutdown)
	}()
	if err := s.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Printf("control server: %v", err)
	}
}

func bridge(ctx context.Context, viewer quic.Connection, alpn string) {
	defer viewer.CloseWithError(0, "closed")
	stream, err := viewer.AcceptStream(ctx)
	if err != nil {
		return
	}
	first, err := readFrame(stream)
	if err != nil {
		return
	}
	circuit, target := findRoute(first)
	if target == "" {
		log.Printf("no registered route in first packet")
		return
	}
	sim, err := quic.DialAddr(ctx, target, &tls.Config{InsecureSkipVerify: true, NextProtos: []string{alpn}}, &quic.Config{KeepAlivePeriod: 20 * time.Second, MaxIdleTimeout: 60 * time.Second})
	if err != nil {
		log.Printf("circuit %d dial %s: %v", circuit, target, err)
		return
	}
	defer sim.CloseWithError(0, "closed")
	simStream, err := sim.OpenStreamSync(ctx)
	if err != nil {
		return
	}
	if err = writeFrame(simStream, first); err != nil {
		return
	}
	done := make(chan struct{}, 2)
	go func() { io.Copy(simStream, stream); simStream.Close(); done <- struct{}{} }()
	go func() { io.Copy(stream, simStream); stream.Close(); done <- struct{}{} }()
	select {
	case <-ctx.Done():
	case <-done:
	}
}

func readFrame(r io.Reader) ([]byte, error) {
	var h [4]byte
	if _, e := io.ReadFull(r, h[:]); e != nil {
		return nil, e
	}
	n := binary.BigEndian.Uint32(h[:])
	if n == 0 || n > 65536 {
		return nil, errors.New("invalid frame")
	}
	b := make([]byte, n)
	_, e := io.ReadFull(r, b)
	return b, e
}
func writeFrame(w io.Writer, b []byte) error {
	var h [4]byte
	binary.BigEndian.PutUint32(h[:], uint32(len(b)))
	if _, e := w.Write(h[:]); e != nil {
		return e
	}
	_, e := w.Write(b)
	return e
}
func findRoute(packet []byte) (uint32, string) {
	routes.RLock()
	defer routes.RUnlock()
	for code, target := range routes.m {
		var b [4]byte
		binary.BigEndian.PutUint32(b[:], code)
		for i := 0; i+4 <= len(packet); i++ {
			if string(packet[i:i+4]) == string(b[:]) {
				return code, target
			}
		}
	}
	return 0, ""
}

// ROBUST owns the write end of this anonymous pipe.  The OS closes it even for
// a crash or forced termination, avoiding unreliable PID reuse/polling races.
func monitorParentPipe(ctx context.Context, pid int, cancel context.CancelFunc) {
	done := make(chan struct{})
	go func() { io.Copy(io.Discard, os.Stdin); close(done) }()
	select {
	case <-ctx.Done():
	case <-done:
		log.Printf("parent %d exited", pid)
		cancel()
	}
}
