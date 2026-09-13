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
	"sort"
	"strconv"
	"strings"
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
	RegionName  string `json:"regionName"`
	RegionID    string `json:"regionId"`
	AgentType   string `json:"agentType"`
}

type routeTarget struct {
	Network string
	Address string
	Seen    time.Time
}

const routeTTL = 30 * time.Minute

func sweepRoutes() {
	routes.Lock()
	defer routes.Unlock()
	cutoff := time.Now().UTC().Add(-routeTTL)
	for code, target := range routes.m {
		if target.Seen.Before(cutoff) {
			delete(routes.m, code)
			log.Printf("expired stale route for circuit %d", code)
		}
	}
}

func routeSweeper(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			sweepRoutes()
		}
	}
}

type regionRequest struct {
	RegionID      string `json:"regionId"`
	RegionName    string `json:"regionName"`
	Host          string `json:"host"`
	SimPort       int    `json:"simPort"`
	RequestedPort int    `json:"requestedPort"`
	QuicPort      int    `json:"quicPort"`
}

type regionLease struct {
	RegionID   string    `json:"regionId"`
	RegionName string    `json:"regionName"`
	Host       string    `json:"host"`
	SimPort    int       `json:"simPort"`
	QuicPort   int       `json:"quicPort"`
	LastSeen   time.Time `json:"lastSeen"`
}

type brainState struct {
	sync.Mutex
	leases     map[string]*regionLease
	ports      map[int]string
	portStart  int
	portEnd    int
	publicPort int
	ttl        time.Duration
	excluded   map[int]bool
}

var routes = struct {
	sync.RWMutex
	m map[uint32]routeTarget
}{m: make(map[uint32]routeTarget)}

func main() {
	parent := flag.Int("parent-pid", 0, "ROBUST process id; 0 enables manual standalone mode")
	port := flag.Int("listen-port", 9001, "public QUIC port")
	control := flag.Int("control-port", 19001, "loopback ROBUST control port")
	brainBind := flag.String("brain-bind", "127.0.0.1", "region brain bind address")
	brainPort := flag.Int("brain-port", 19002, "region brain HTTP port; 0 disables")
	regionPortStart := flag.Int("region-port-start", 22000, "first auto-assignable simulator QUIC port")
	regionPortEnd := flag.Int("region-port-end", 22500, "last auto-assignable simulator QUIC port")
	regionPortExclude := flag.String("region-port-exclude", "", "comma-separated simulator QUIC ports never to allocate")
	leaseSeconds := flag.Int("brain-lease-seconds", 90, "region lease expiry in seconds")
	cert := flag.String("cert", "SSL/quic/quic-cert.pem", "PEM certificate")
	key := flag.String("key", "SSL/quic/quic-key.pem", "PEM private key")
	alpn := flag.String("alpn", "opensim-ll/1", "QUIC ALPN")
	flag.Parse()

	if *port <= 0 || *port > 65535 {
		log.Fatalf("invalid --listen-port %d", *port)
	}
	if *control <= 0 || *control > 65535 {
		log.Fatalf("invalid --control-port %d", *control)
	}
	if *brainPort < 0 || *brainPort > 65535 {
		log.Fatalf("invalid --brain-port %d", *brainPort)
	}
	if *brainPort != 0 && *brainPort == *control {
		log.Fatal("--brain-port and --control-port must be different")
	}
	if *regionPortStart <= 0 || *regionPortEnd > 65535 || *regionPortStart > *regionPortEnd {
		log.Fatalf("invalid region port range %d-%d", *regionPortStart, *regionPortEnd)
	}
	if *leaseSeconds < 15 {
		*leaseSeconds = 15
	}

	excluded, err := parseExcludedPorts(*regionPortExclude)
	if err != nil {
		log.Fatalf("invalid --region-port-exclude: %v", err)
	}
	brain := &brainState{
		leases:     make(map[string]*regionLease),
		ports:      make(map[int]string),
		portStart:  *regionPortStart,
		portEnd:    *regionPortEnd,
		publicPort: *port,
		ttl:        time.Duration(*leaseSeconds) * time.Second,
		excluded:   excluded,
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	if *parent > 0 {
		go monitorParent(ctx, *parent, cancel)
	} else {
		log.Printf("Quick-G manual mode: no ROBUST parent supplied; waiting for ROBUST control/shutdown")
	}

	pair, err := tls.LoadX509KeyPair(*cert, *key)
	if err != nil {
		log.Fatalf("load TLS key pair: %v", err)
	}
	listener, err := quic.ListenAddr(fmt.Sprintf(":%d", *port), &tls.Config{Certificates: []tls.Certificate{pair}, NextProtos: []string{*alpn}}, &quic.Config{KeepAlivePeriod: 20 * time.Second, MaxIdleTimeout: 60 * time.Second})
	if err != nil {
		log.Fatalf("listen: %v", err)
	}
	defer listener.Close()

	go serveControl(ctx, *control, *brainPort, cancel, *port, *regionPortStart, *regionPortEnd, excluded)
	if *brainPort != 0 {
		go serveBrain(ctx, *brainBind, *brainPort, brain)
		go brain.reaper(ctx)
	}
	go func() { <-ctx.Done(); listener.Close() }()
	go routeSweeper(ctx)

	if *parent > 0 {
		log.Printf("Quick-G listening on UDP %d (parent %d)", *port, *parent)
	} else {
		log.Printf("Quick-G listening on UDP %d (manual mode)", *port)
	}
	if *brainPort != 0 {
		log.Printf("Quick-G brain listening on %s:%d, region QUIC pool %d-%d", *brainBind, *brainPort, *regionPortStart, *regionPortEnd)
	}

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

func parseExcludedPorts(value string) (map[int]bool, error) {
	result := make(map[int]bool)
	for _, item := range strings.Split(value, ",") {
		item = strings.TrimSpace(item)
		if item == "" {
			continue
		}
		port, err := strconv.Atoi(item)
		if err != nil || port <= 0 || port > 65535 {
			return nil, fmt.Errorf("%q is not a valid port", item)
		}
		result[port] = true
	}
	return result, nil
}

func serveControl(ctx context.Context, port int, brainPort int, cancel context.CancelFunc, listenPort, poolStart, poolEnd int, excluded map[int]bool) {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		routes.RLock()
		routeCount := len(routes.m)
		routes.RUnlock()
		writeJSON(w, http.StatusOK, map[string]any{
			"ready":     true,
			"routes":    routeCount,
			"brainPort": brainPort,
		})
	})
	mux.HandleFunc("/shutdown", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"stopping": true})
		go cancel()
	})
	mux.HandleFunc("/register", func(w http.ResponseWriter, r *http.Request) {
		var v registration
		if r.Method != http.MethodPost || json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&v) != nil || v.CircuitCode == 0 {
			http.Error(w, "bad registration", http.StatusBadRequest)
			return
		}
		network := "udp"
		host, targetPort := v.SimHost, v.SimPort
		regionTag := v.RegionName
		if regionTag == "" {
			regionTag = v.RegionID
		}
		if regionTag == "" {
			regionTag = "unknown-region"
		}
		if v.QuicPort > 0 {
			if v.QuicPort == listenPort || v.QuicPort < poolStart || v.QuicPort > poolEnd || excluded[v.QuicPort] {
				log.Printf("register circuit %d region %s: REJECTED stale quicPort %d (pool %d-%d); keeping UDP backend %s:%d", v.CircuitCode, regionTag, v.QuicPort, poolStart, poolEnd, v.SimHost, v.SimPort)
			} else {
				network = "quic"
				host, targetPort = v.QuicHost, v.QuicPort
				if host == "" {
					host = v.SimHost
				}
			}
		}
		if network == "udp" {
			host = localBackendHost(host)
		}
		if host == "" || targetPort <= 0 || targetPort > 65535 {
			http.Error(w, "bad endpoint", http.StatusBadRequest)
			return
		}
		newAddr := net.JoinHostPort(host, strconv.Itoa(targetPort))
		if v.AgentType == "child" {
			routes.RLock()
			existing, ok := routes.m[v.CircuitCode]
			routes.RUnlock()
			if ok && existing.Address != newAddr {
				log.Printf("register circuit %d region %s: IGNORING child-agent flip %s -> %s %s; keeping root route", v.CircuitCode, regionTag, existing.Address, network, newAddr)
				writeJSON(w, http.StatusOK, map[string]any{"success": true})
				return
			}
		}
		routes.Lock()
		routes.m[v.CircuitCode] = routeTarget{Network: network, Address: newAddr, Seen: time.Now().UTC()}
		routes.Unlock()
		log.Printf("register circuit %d -> %s %s region %s", v.CircuitCode, network, newAddr, regionTag)
		writeJSON(w, http.StatusOK, map[string]any{"success": true})
	})
	mux.HandleFunc("/unregister", func(w http.ResponseWriter, r *http.Request) {
		var v registration
		if r.Method != http.MethodPost || json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&v) != nil || v.CircuitCode == 0 {
			http.Error(w, "bad registration", http.StatusBadRequest)
			return
		}
		routes.Lock()
		delete(routes.m, v.CircuitCode)
		routes.Unlock()
		writeJSON(w, http.StatusOK, map[string]any{"success": true})
	})

	s := &http.Server{Addr: net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), Handler: mux, ReadHeaderTimeout: 3 * time.Second}
	go func() {
		<-ctx.Done()
		shutdown, stop := context.WithTimeout(context.Background(), time.Second)
		defer stop()
		_ = s.Shutdown(shutdown)
	}()
	if err := s.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Printf("control server: %v", err)
	}
}

func localBackendHost(host string) string {
	// UDP backends are always same-host simulators in this deployment.
	// Force loopback: the registered simHost is the public/advertised
	// address (Pangolin gateway IP), and sending simulator traffic out
	// through the gateway hairpin blackholes it. The sim LLUDP socket
	// binds 0.0.0.0 so loopback delivery always works.
	_ = strings.TrimSpace(host)
	return "127.0.0.1"
}

func serveBrain(ctx context.Context, bind string, port int, brain *brainState) {
	mux := http.NewServeMux()
	mux.HandleFunc("/allocate", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		var request regionRequest
		if json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&request) != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		lease, err := brain.allocate(request)
		if err != nil {
			writeJSON(w, http.StatusServiceUnavailable, map[string]any{"success": false, "error": err.Error()})
			return
		}
		log.Printf("brain lease: %s (%s) -> QUIC %d", lease.RegionName, lease.RegionID, lease.QuicPort)
		writeJSON(w, http.StatusOK, map[string]any{"success": true, "quicPort": lease.QuicPort, "leaseSeconds": int(brain.ttl.Seconds())})
	})
	mux.HandleFunc("/heartbeat", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		var request regionRequest
		if json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&request) != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		lease, err := brain.heartbeat(request)
		if err != nil {
			writeJSON(w, http.StatusConflict, map[string]any{"success": false, "error": err.Error()})
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"success": true, "quicPort": lease.QuicPort})
	})
	mux.HandleFunc("/release", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		var request regionRequest
		if json.NewDecoder(io.LimitReader(r.Body, 8192)).Decode(&request) != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		brain.release(request)
		writeJSON(w, http.StatusOK, map[string]any{"success": true})
	})
	mux.HandleFunc("/status", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"ready":      true,
			"portStart":  brain.portStart,
			"portEnd":    brain.portEnd,
			"leaseCount": brain.count(),
			"leases":     brain.snapshot(),
		})
	})

	s := &http.Server{Addr: net.JoinHostPort(bind, strconv.Itoa(port)), Handler: mux, ReadHeaderTimeout: 3 * time.Second}
	go func() {
		<-ctx.Done()
		shutdown, stop := context.WithTimeout(context.Background(), time.Second)
		defer stop()
		_ = s.Shutdown(shutdown)
	}()
	if err := s.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Printf("brain server: %v", err)
	}
}

func (b *brainState) allocate(request regionRequest) (*regionLease, error) {
	key := regionKey(request)
	if key == "" {
		return nil, errors.New("regionId or host/simPort is required")
	}

	b.Lock()
	defer b.Unlock()
	now := time.Now().UTC()
	b.pruneLocked(now)

	if lease := b.leases[key]; lease != nil {
		lease.RegionName = request.RegionName
		lease.Host = request.Host
		lease.SimPort = request.SimPort
		lease.LastSeen = now
		copy := *lease
		return &copy, nil
	}

	selected := request.RequestedPort
	if selected != 0 {
		if err := b.portAvailableLocked(selected, key); err != nil {
			return nil, err
		}
	} else {
		for candidate := b.portStart; candidate <= b.portEnd; candidate++ {
			if b.excluded[candidate] || candidate == b.publicPort {
				continue
			}
			if _, used := b.ports[candidate]; !used {
				selected = candidate
				break
			}
		}
		if selected == 0 {
			return nil, errors.New("region QUIC port pool is exhausted")
		}
	}

	lease := &regionLease{
		RegionID:   request.RegionID,
		RegionName: request.RegionName,
		Host:       request.Host,
		SimPort:    request.SimPort,
		QuicPort:   selected,
		LastSeen:   now,
	}
	b.leases[key] = lease
	b.ports[selected] = key
	copy := *lease
	return &copy, nil
}

func (b *brainState) heartbeat(request regionRequest) (*regionLease, error) {
	key := regionKey(request)
	if key == "" {
		return nil, errors.New("regionId or host/simPort is required")
	}

	b.Lock()
	defer b.Unlock()
	now := time.Now().UTC()
	b.pruneLocked(now)
	if lease := b.leases[key]; lease != nil {
		if request.QuicPort != 0 && request.QuicPort != lease.QuicPort {
			return nil, fmt.Errorf("region already owns QUIC port %d", lease.QuicPort)
		}
		lease.RegionName = request.RegionName
		lease.Host = request.Host
		lease.SimPort = request.SimPort
		lease.LastSeen = now
		copy := *lease
		return &copy, nil
	}

	// Re-adopt a running region after a Quick-G brain restart. The region sends
	// its current listener port in the heartbeat, so no simulator restart is needed.
	if request.QuicPort == 0 {
		return nil, errors.New("lease not found; heartbeat must include quicPort")
	}
	if err := b.portAvailableLocked(request.QuicPort, key); err != nil {
		return nil, err
	}
	lease := &regionLease{
		RegionID:   request.RegionID,
		RegionName: request.RegionName,
		Host:       request.Host,
		SimPort:    request.SimPort,
		QuicPort:   request.QuicPort,
		LastSeen:   now,
	}
	b.leases[key] = lease
	b.ports[request.QuicPort] = key
	copy := *lease
	return &copy, nil
}

func (b *brainState) release(request regionRequest) {
	key := regionKey(request)
	if key == "" {
		return
	}
	b.Lock()
	defer b.Unlock()
	if lease := b.leases[key]; lease != nil {
		delete(b.ports, lease.QuicPort)
		delete(b.leases, key)
		log.Printf("brain release: %s (%s) QUIC %d", lease.RegionName, lease.RegionID, lease.QuicPort)
	}
}

func (b *brainState) portAvailableLocked(port int, key string) error {
	if port < b.portStart || port > b.portEnd {
		return fmt.Errorf("requested port %d is outside pool %d-%d", port, b.portStart, b.portEnd)
	}
	if b.excluded[port] || port == b.publicPort {
		return fmt.Errorf("requested port %d is reserved", port)
	}
	if owner, used := b.ports[port]; used && owner != key {
		return fmt.Errorf("requested port %d is already leased", port)
	}
	return nil
}

func (b *brainState) reaper(ctx context.Context) {
	interval := 30 * time.Second
	if b.ttl/3 < interval {
		interval = b.ttl / 3
	}
	if interval < 5*time.Second {
		interval = 5 * time.Second
	}
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			b.Lock()
			b.pruneLocked(now.UTC())
			b.Unlock()
		}
	}
}

func (b *brainState) pruneLocked(now time.Time) {
	for key, lease := range b.leases {
		if now.Sub(lease.LastSeen) > b.ttl {
			delete(b.ports, lease.QuicPort)
			delete(b.leases, key)
			log.Printf("brain lease expired: %s (%s) QUIC %d", lease.RegionName, lease.RegionID, lease.QuicPort)
		}
	}
}

func (b *brainState) count() int {
	b.Lock()
	defer b.Unlock()
	b.pruneLocked(time.Now().UTC())
	return len(b.leases)
}

func (b *brainState) snapshot() []regionLease {
	b.Lock()
	defer b.Unlock()
	b.pruneLocked(time.Now().UTC())
	result := make([]regionLease, 0, len(b.leases))
	for _, lease := range b.leases {
		result = append(result, *lease)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].QuicPort < result[j].QuicPort })
	return result
}

func regionKey(request regionRequest) string {
	if id := strings.TrimSpace(request.RegionID); id != "" {
		return "id:" + strings.ToLower(id)
	}
	if host := strings.TrimSpace(request.Host); host != "" && request.SimPort > 0 {
		return "endpoint:" + strings.ToLower(host) + ":" + strconv.Itoa(request.SimPort)
	}
	return ""
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
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
	if target.Address == "" {
		log.Printf("no registered route in first packet len=%d head=%s", len(first), packetHead(first, 16))
		return
	}
	if target.Network == "udp" {
		bridgeUDP(ctx, circuit, stream, first, target.Address)
		return
	}
	bridgeQUIC(ctx, circuit, stream, first, target.Address, alpn)
}

func bridgeQUIC(ctx context.Context, circuit uint32, stream quic.Stream, first []byte, target string, alpn string) {
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

func bridgeUDP(ctx context.Context, circuit uint32, stream quic.Stream, first []byte, target string) {
	udpAddr, err := net.ResolveUDPAddr("udp", target)
	if err != nil {
		log.Printf("circuit %d resolve udp %s: %v", circuit, target, err)
		return
	}
	udp, err := net.DialUDP("udp", nil, udpAddr)
	if err != nil {
		log.Printf("circuit %d dial udp %s: %v", circuit, target, err)
		return
	}
	defer udp.Close()
	if _, err = udp.Write(first); err != nil {
		log.Printf("circuit %d udp write first %s: %v", circuit, target, err)
		return
	}
	done := make(chan struct{}, 2)
	go func() {
		defer func() { done <- struct{}{} }()
		for {
			packet, err := readFrame(stream)
			if err != nil {
				return
			}
			if _, err = udp.Write(packet); err != nil {
				log.Printf("circuit %d udp write %s: %v", circuit, target, err)
				return
			}
		}
	}()
	go func() {
		defer func() { done <- struct{}{} }()
		buf := make([]byte, 65536)
		for {
			_ = udp.SetReadDeadline(time.Now().Add(time.Second))
			n, err := udp.Read(buf)
			if err != nil {
				if ne, ok := err.(net.Error); ok && ne.Timeout() {
					select {
					case <-ctx.Done():
						return
					default:
						continue
					}
				}
				return
			}
			if err = writeFrame(stream, buf[:n]); err != nil {
				return
			}
		}
	}()
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

func findRoute(packet []byte) (uint32, routeTarget) {
	routes.RLock()
	defer routes.RUnlock()
	if len(routes.m) == 1 {
		for code, target := range routes.m {
			log.Printf("single pending route fallback for first packet len=%d head=%s -> circuit %d", len(packet), packetHead(packet, 16), code)
			return code, target
		}
	}
	for code, target := range routes.m {
		var be [4]byte
		var le [4]byte
		binary.BigEndian.PutUint32(be[:], code)
		binary.LittleEndian.PutUint32(le[:], code)
		for i := 0; i+4 <= len(packet); i++ {
			if string(packet[i:i+4]) == string(be[:]) || string(packet[i:i+4]) == string(le[:]) {
				return code, target
			}
		}
	}
	return 0, routeTarget{}
}

func packetHead(packet []byte, max int) string {
	if max > len(packet) {
		max = len(packet)
	}
	if max <= 0 {
		return ""
	}
	parts := make([]string, 0, max)
	for _, b := range packet[:max] {
		parts = append(parts, fmt.Sprintf("%02x", b))
	}
	return strings.Join(parts, " ")
}
