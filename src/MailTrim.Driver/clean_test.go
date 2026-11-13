package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"
)

func TestCleanLetters(t *testing.T) {
	rows := cleanLetters([]letter{
		{URL: "https://evil.test/mail", Sender: "bad"},
		{URL: "https://e.mail.ru/inbox/1", Sender: strings.Repeat("я", 600), Subject: "subject"},
	})
	if len(rows) != 1 || len([]rune(rows[0].Sender)) != 500 {
		t.Fatalf("unexpected sanitized letters: %#v", rows)
	}
}

func TestCleanBlocks(t *testing.T) {
	rows := cleanBlocks([]readerBlock{
		{Text: "safe", Image: "javascript:alert(1)"},
		{Text: "", Image: "https://example.org/image.png"},
		{Text: "secret", Image: "https://user:pass@example.org/a.png"},
	})
	if len(rows) != 3 || rows[0].Image != "" || rows[1].Image == "" || rows[2].Image != "" {
		t.Fatalf("unexpected sanitized blocks: %#v", rows)
	}
}

func TestProtocolDoesNotEchoUnknownData(t *testing.T) {
	input := bytes.NewBufferString(`{"id":7,"op":"clean_blocks","token":"never-echo","blocks":[{"text":"hello","image":""}]}`)
	var output bytes.Buffer
	if err := run(input, &output); err != nil {
		t.Fatal(err)
	}
	var answer map[string]any
	if err := json.Unmarshal(output.Bytes(), &answer); err != nil {
		t.Fatal(err)
	}
	if answer["ok"] != true || strings.Contains(output.String(), "never-echo") {
		t.Fatalf("unexpected response: %s", output.String())
	}
}
