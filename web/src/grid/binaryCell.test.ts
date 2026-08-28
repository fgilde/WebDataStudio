import { describe, it, expect } from "vitest";
import { fileNameFor, looksBinary, size, sniff, toBytes, toHex } from "./binaryCell";

const bytes = (...values: number[]) => new Uint8Array(values);

describe("bytes in a cell", () => {
  it("recognises the form a binary column travels in", () => {
    expect(looksBinary("0x89504e47")).toBe(true);
    expect(looksBinary("0X89")).toBe(true);

    expect(looksBinary("0x")).toBe(false);        // nothing in it
    expect(looksBinary("0x8")).toBe(false);       // half a byte
    expect(looksBinary("0xzz")).toBe(false);
    expect(looksBinary("hello")).toBe(false);
    expect(looksBinary(42)).toBe(false);
  });

  it("goes to bytes and back without changing", () => {
    expect(toHex(toBytes("0x89504e47"))).toBe("0x89504e47");
    expect([...toBytes("0x00ff")]).toEqual([0, 255]);
  });

  /// The point of the whole thing: a PDF saved as column.txt is a file nobody can open.
  it("reads what a blob is from its first bytes", () => {
    expect(sniff(bytes(0x89, 0x50, 0x4e, 0x47)).extension).toBe("png");
    expect(sniff(bytes(0xff, 0xd8, 0xff)).mime).toBe("image/jpeg");
    expect(sniff(bytes(0x25, 0x50, 0x44, 0x46)).extension).toBe("pdf");
    expect(sniff(bytes(0x50, 0x4b, 0x03, 0x04)).extension).toBe("zip");
    expect(sniff(bytes(0x1f, 0x8b)).extension).toBe("gz");
  });

  it("calls what it cannot name a bin file rather than guessing", () => {
    expect(sniff(bytes(1, 2, 3, 4)).extension).toBe("bin");
    expect(sniff(bytes(1, 2, 3, 4)).mime).toBe("application/octet-stream");
  });

  it("names the file after the column and what is in it", () => {
    expect(fileNameFor("avatar", bytes(0x89, 0x50, 0x4e, 0x47))).toBe("avatar.png");
    // A column name is not a file name until the characters a file system dislikes are gone.
    expect(fileNameFor("my col/1", bytes(1))).toBe("my_col_1.bin");
  });

  it("says how big it is in the words somebody would use", () => {
    expect(size(512)).toBe("512 bytes");
    expect(size(2048)).toBe("2.0 kB");
    expect(size(3 * 1024 * 1024)).toBe("3.0 MB");
  });
});
