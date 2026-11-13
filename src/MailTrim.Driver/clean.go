package main

import (
	"net/url"
	"strings"
	"time"
	"unicode/utf16"
)

type letter struct {
	URL        string     `json:"url"`
	Sender     string     `json:"sender"`
	Subject    string     `json:"subject"`
	Preview    string     `json:"preview"`
	Date       string     `json:"date"`
	ReceivedAt *time.Time `json:"receivedAt,omitempty"`
	CapturedAt *time.Time `json:"capturedAt,omitempty"`
	DateHint   string     `json:"dateHint"`
	Unread     *bool      `json:"unread,omitempty"`
	IsGroup    bool       `json:"isGroup"`
}

type readerBlock struct {
	Text  string `json:"text"`
	Image string `json:"image"`
}

func truncate(value string, limit int) string {
	encoded := utf16.Encode([]rune(value))
	if len(encoded) <= limit {
		return value
	}
	encoded = encoded[:limit]
	if limit > 0 && encoded[limit-1] >= 0xD800 && encoded[limit-1] <= 0xDBFF {
		encoded = encoded[:limit-1]
	}
	return string(utf16.Decode(encoded))
}

func textLength(value string) int { return len(utf16.Encode([]rune(value))) }

func mailURL(value string) bool {
	u, err := url.Parse(value)
	return err == nil && u.Scheme == "https" && u.Hostname() == "e.mail.ru" && u.User == nil && (u.Port() == "" || u.Port() == "443")
}

func imageURL(value string) bool {
	if len(value) > 4000 {
		return false
	}
	u, err := url.Parse(value)
	return err == nil && u.Scheme == "https" && u.Hostname() != "" && u.User == nil && (u.Port() == "" || u.Port() == "443")
}

func cleanLetters(input []letter) []letter {
	if len(input) > 50000 {
		input = input[:50000]
	}
	result := make([]letter, 0, len(input))
	for _, item := range input {
		if !mailURL(item.URL) {
			continue
		}
		item.Sender = truncate(item.Sender, 500)
		item.Subject = truncate(item.Subject, 2000)
		item.Preview = truncate(item.Preview, 300)
		item.Date = truncate(item.Date, 100)
		item.DateHint = truncate(item.DateHint, 150)
		result = append(result, item)
	}
	return result
}

func cleanBlocks(input []readerBlock) []readerBlock {
	if len(input) > 300 {
		input = input[:300]
	}
	result := make([]readerBlock, 0, len(input))
	remaining := 500000
	for _, item := range input {
		if remaining <= 0 {
			break
		}
		textLimit := 20000
		if remaining < textLimit {
			textLimit = remaining
		}
		item.Text = truncate(item.Text, textLimit)
		remaining -= textLength(item.Text)
		if !imageURL(item.Image) || textLength(item.Image) > remaining {
			item.Image = ""
		}
		remaining -= textLength(item.Image)
		if strings.TrimSpace(item.Text) != "" || item.Image != "" {
			result = append(result, item)
		}
	}
	return result
}
