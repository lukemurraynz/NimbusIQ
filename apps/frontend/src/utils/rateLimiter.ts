/**
 * Client-side rate limiter using token bucket algorithm
 * Addresses CYBER-001/002: Rate limiting for API calls
 */

export interface RateLimiterOptions {
  /** Maximum number of tokens (requests) available */
  capacity: number;
  /** Number of tokens refilled per interval */
  refillRate: number;
  /** Refill interval in milliseconds */
  refillInterval: number;
}

/**
 * Token bucket rate limiter
 * Prevents client from overwhelming server with too many requests
 */
export class RateLimiter {
  private tokens: number;
  private lastRefill: number;
  private readonly options: RateLimiterOptions;

  constructor(options: Partial<RateLimiterOptions> = {}) {
    this.options = {
      capacity: options.capacity ?? 50,
      refillRate: options.refillRate ?? 10,
      refillInterval: options.refillInterval ?? 1000, // 1 second
    };
    this.tokens = this.options.capacity;
    this.lastRefill = Date.now();
  }

  /**
   * Attempt to consume a token
   * Returns true if request is allowed, false if rate limited
   */
  tryConsume(): boolean {
    this.refill();

    if (this.tokens >= 1) {
      this.tokens -= 1;
      return true;
    }

    return false;
  }

  /**
   * Wait until a token is available, then consume it
   * Returns a Promise that resolves when request is allowed
   */
  async consume(): Promise<void> {
    while (!this.tryConsume()) {
      // Wait for next refill interval
      await new Promise((resolve) =>
        setTimeout(resolve, this.options.refillInterval / 2),
      );
    }
  }

  /**
   * Refill tokens based on elapsed time
   */
  private refill(): void {
    const now = Date.now();
    const elapsed = now - this.lastRefill;
    const intervalsElapsed = Math.floor(elapsed / this.options.refillInterval);

    if (intervalsElapsed > 0) {
      const tokensToAdd = intervalsElapsed * this.options.refillRate;
      this.tokens = Math.min(this.options.capacity, this.tokens + tokensToAdd);
      this.lastRefill = now;
    }
  }

  /**
   * Get current token count (for debugging/monitoring)
   */
  getAvailableTokens(): number {
    this.refill();
    return this.tokens;
  }

  /**
   * Reset the rate limiter (for testing)
   */
  reset(): void {
    this.tokens = this.options.capacity;
    this.lastRefill = Date.now();
  }
}
