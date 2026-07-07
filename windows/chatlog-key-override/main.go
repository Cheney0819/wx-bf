package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"runtime"
	"strings"

	"github.com/sjzar/chatlog/internal/wechat/decrypt"
	"github.com/sjzar/chatlog/internal/wechat/key"
	wechatmodel "github.com/sjzar/chatlog/internal/wechat/model"
	wechatprocess "github.com/sjzar/chatlog/internal/wechat/process"
)

func main() {
	pid := flag.Int("pid", 0, "wechat pid")
	dataDir := flag.String("data-dir", strings.TrimSpace(os.Getenv("WX_CHATLOG_DATA_DIR_OVERRIDE")), "override data dir")
	flag.Parse()

	if *pid <= 0 {
		fmt.Fprintln(os.Stderr, "pid is required")
		os.Exit(1)
	}

	proc, err := findProcessByPID(uint32(*pid))
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}

	overrideDir := strings.TrimSpace(*dataDir)
	if overrideDir != "" {
		proc.DataDir = overrideDir
	}
	if strings.TrimSpace(proc.DataDir) == "" {
		fmt.Fprintln(os.Stderr, "data dir is empty")
		os.Exit(1)
	}

	extractor, err := key.NewExtractor(proc.Platform, proc.Version)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}

	validator, err := decrypt.NewValidator(proc.Platform, proc.Version, proc.DataDir)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}

	extractor.SetValidate(validator)
	hexKey, err := extractor.Extract(context.Background(), proc)
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}

	fmt.Println(hexKey)
}

func findProcessByPID(pid uint32) (*wechatmodel.Process, error) {
	detector := wechatprocess.NewDetector(runtime.GOOS)
	processes, err := detector.FindProcesses()
	if err != nil {
		return nil, err
	}

	for _, proc := range processes {
		if proc.PID == pid {
			return proc, nil
		}
	}

	return nil, errors.New("wechat process not found")
}
