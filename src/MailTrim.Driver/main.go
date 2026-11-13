package main

import (
	"bufio"
	"encoding/json"
	"io"
	"os"
)

type request struct {
	ID      uint64        `json:"id"`
	Op      string        `json:"op"`
	Letters []letter      `json:"letters"`
	Blocks  []readerBlock `json:"blocks"`
}

type response struct {
	ID      uint64        `json:"id"`
	OK      bool          `json:"ok"`
	Letters []letter      `json:"letters,omitempty"`
	Blocks  []readerBlock `json:"blocks,omitempty"`
	Error   string        `json:"error,omitempty"`
}

func handle(r request) response {
	answer := response{ID: r.ID, OK: true}
	switch r.Op {
	case "clean_letters":
		answer.Letters = cleanLetters(r.Letters)
	case "clean_blocks":
		answer.Blocks = cleanBlocks(r.Blocks)
	case "ping":
	default:
		answer.OK = false
		answer.Error = "unsupported_operation"
	}
	return answer
}

func run(input io.Reader, output io.Writer) error {
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 64*1024), 8<<20)
	encoder := json.NewEncoder(output)
	for scanner.Scan() {
		var r request
		if err := json.Unmarshal(scanner.Bytes(), &r); err != nil {
			return err
		}
		if err := encoder.Encode(handle(r)); err != nil {
			return err
		}
	}
	return scanner.Err()
}

func main() {
	writer := bufio.NewWriterSize(os.Stdout, 64*1024)
	err := run(bufio.NewReaderSize(os.Stdin, 64*1024), writer)
	_ = writer.Flush()
	if err != nil {
		os.Exit(1)
	}
}
