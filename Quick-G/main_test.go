package main

import (
	"testing"
	"time"
)

func newTestBrain() *brainState {
	return &brainState{
		leases:     make(map[string]*regionLease),
		ports:      make(map[int]string),
		portStart:  22000,
		portEnd:    22005,
		publicPort: 9001,
		ttl:        90 * time.Second,
		excluded:   map[int]bool{22002: true},
	}
}

func TestBrainAllocatesUniquePorts(t *testing.T) {
	brain := newTestBrain()

	first, err := brain.allocate(regionRequest{RegionID: "region-a", RegionName: "A"})
	if err != nil {
		t.Fatal(err)
	}
	second, err := brain.allocate(regionRequest{RegionID: "region-b", RegionName: "B"})
	if err != nil {
		t.Fatal(err)
	}

	if first.QuicPort != 22000 {
		t.Fatalf("first port = %d, want 22000", first.QuicPort)
	}
	if second.QuicPort != 22001 {
		t.Fatalf("second port = %d, want 22001", second.QuicPort)
	}
}

func TestBrainReusesLeaseForSameRegion(t *testing.T) {
	brain := newTestBrain()

	first, err := brain.allocate(regionRequest{RegionID: "region-a", RegionName: "A"})
	if err != nil {
		t.Fatal(err)
	}
	second, err := brain.allocate(regionRequest{RegionID: "region-a", RegionName: "A restarted"})
	if err != nil {
		t.Fatal(err)
	}

	if second.QuicPort != first.QuicPort {
		t.Fatalf("reallocated region changed port from %d to %d", first.QuicPort, second.QuicPort)
	}
}

func TestBrainSkipsExcludedPort(t *testing.T) {
	brain := newTestBrain()

	for _, id := range []string{"a", "b", "c"} {
		if _, err := brain.allocate(regionRequest{RegionID: id}); err != nil {
			t.Fatal(err)
		}
	}

	leases := brain.snapshot()
	for _, lease := range leases {
		if lease.QuicPort == 22002 {
			t.Fatal("allocator used excluded port 22002")
		}
	}
}

func TestBrainHeartbeatReadoptsRunningRegion(t *testing.T) {
	brain := newTestBrain()

	lease, err := brain.heartbeat(regionRequest{
		RegionID:   "region-a",
		RegionName: "A",
		Host:       "127.0.0.1",
		SimPort:    20000,
		QuicPort:   22004,
	})
	if err != nil {
		t.Fatal(err)
	}
	if lease.QuicPort != 22004 {
		t.Fatalf("heartbeat adopted %d, want 22004", lease.QuicPort)
	}

	again, err := brain.allocate(regionRequest{RegionID: "region-a"})
	if err != nil {
		t.Fatal(err)
	}
	if again.QuicPort != 22004 {
		t.Fatalf("adopted lease changed to %d", again.QuicPort)
	}
}

func TestBrainReleaseMakesPortAvailable(t *testing.T) {
	brain := newTestBrain()

	lease, err := brain.allocate(regionRequest{RegionID: "region-a"})
	if err != nil {
		t.Fatal(err)
	}
	brain.release(regionRequest{RegionID: "region-a"})

	next, err := brain.allocate(regionRequest{RegionID: "region-b"})
	if err != nil {
		t.Fatal(err)
	}
	if next.QuicPort != lease.QuicPort {
		t.Fatalf("released port %d was not reused; got %d", lease.QuicPort, next.QuicPort)
	}
}
