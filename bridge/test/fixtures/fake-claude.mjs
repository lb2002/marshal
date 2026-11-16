// Stand-in for the claude CLI in runner tests. The last argv element (the prompt) picks the behaviour.
const prompt = process.argv[process.argv.length - 1];
if (prompt === "sleep" || prompt.includes("lotse-fixture-sleep")) {
  setInterval(() => {}, 1000);
} else if (prompt === "fail") {
  process.stderr.write("PRIVATE-STDERR-TEXT from the prompt\n");
  process.exit(3);
} else if (prompt === "flood") {
  const chunk = "x".repeat(65536);
  const write = () => {
    while (process.stdout.write(chunk));
    process.stdout.once("drain", write);
  };
  write();
} else {
  process.stdout.write(JSON.stringify({ cwd: process.cwd(), args: process.argv.slice(2) }));
}
