// Alias launcher for Live Photo Box CLI — resolves symlinks then execs livephotobox.exe.
//
// winget creates symlinks in %LOCALAPPDATA%\Microsoft\WinGet\Links\ for each
// PortableCommandAlias. The .NET apphost (GetModuleFileNameW) does NOT resolve
// symlinks, so it looks for livephotobox.dll in Links/ instead of the real
// Packages/ directory → "application to execute does not exist".
//
// This Go shim resolves the symlink to the real path, locates livephotobox.exe
// in the same directory, and spawns it with full argument/stdin/stdout/stderr
// passthrough.
//
// Build: go build -ldflags="-s -w" -o <alias>.exe scripts\alias-launcher.go

package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

func main() {
	exe, err := os.Executable()
	if err != nil {
		fmt.Fprintf(os.Stderr, "alias-launcher: cannot get own path: %v\r\n", err)
		os.Exit(1)
	}

	// Resolve the symlink to get the real binary location.
	realPath, err := filepath.EvalSymlinks(exe)
	if err != nil {
		fmt.Fprintf(os.Stderr, "alias-launcher: cannot resolve symlink: %v\r\n", err)
		os.Exit(1)
	}

	dir := filepath.Dir(realPath)
	target := filepath.Join(dir, "livephotobox-boot.exe")

	cmd := exec.Command(target, os.Args[1:]...)
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	err = cmd.Run()
	if err != nil {
		if exiterr, ok := err.(*exec.ExitError); ok {
			if status, ok := exiterr.Sys().(syscall.WaitStatus); ok {
				os.Exit(status.ExitStatus())
			}
		}
		fmt.Fprintf(os.Stderr, "alias-launcher: cannot exec livephotobox.exe: %v\r\n", err)
		os.Exit(1)
	}
}
